using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentKit.Social.Internal;

/// <summary>
/// Mastodon's REST API with an access token.
/// <list type="bullet">
/// <item>Post: <c>POST /api/v1/statuses</c>, form fields <c>status</c>, <c>visibility</c>, <c>language</c>,
/// <c>media_ids[]</c>, with an <c>Idempotency-Key</c> the server keeps for up to an hour
/// (https://docs.joinmastodon.org/methods/statuses/#create).</item>
/// <item>Length: the instance's <c>max_characters</c>, every URL counted as <c>characters_reserved_per_url</c>
/// (https://docs.joinmastodon.org/methods/instance/#v2).</item>
/// <item>Card: the instance builds the link's preview card from the page itself; a card picture is uploaded only when
/// asked, with
/// <c>POST /api/v2/media</c> (https://docs.joinmastodon.org/methods/media/#v2).</item>
/// <item>Errors: <c>{ "error": "…" }</c>; rate limits in <c>X-RateLimit-Reset</c>, "a timestamp"
/// (https://docs.joinmastodon.org/api/rate-limits/; read as an ISO 8601 date or Unix seconds).</item>
/// </list>
/// </summary>
internal sealed class MastodonChannel : ChannelBase
{
    /// <summary>Mastodon keeps idempotency keys "for up to 1 hour"; resending stays well inside that.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(50);

    /// <summary>Mastodon's limit on a picture's description (alt text).</summary>
    private const int MaxDescription = 1500;

    private readonly MastodonAccount _account;
    private readonly Uri _instance;

    public MastodonChannel(SocialChannelContext context, MastodonAccount account) : base(context, [account.AccessToken])
    {
        _account = account;
        if (Fetch.PublicAddress.Refusal(account.Instance, httpsOnly: true) is { } refusal)
            throw new ArgumentException($"The Mastodon instance address is refused: {refusal}", nameof(account));
        if (account.MaxCharacters < 1 || account.CharactersReservedPerUrl < 0)
            throw new ArgumentException("The Mastodon limits must be positive.", nameof(account));
        _instance = new Uri(account.Instance.GetLeftPart(UriPartial.Authority));
    }

    public override string Network => SocialNetworks.Mastodon;

    public override string Id => "mastodon:" + _instance.Host;

    public override SocialIdempotency Idempotency { get; } = new(SocialIdempotencyKind.KeyWindow, Window,
        "Mastodon drops a repeat of the same Idempotency-Key for up to an hour; later, a retry can't confirm the first try.");

    public override SocialComposition Compose(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        var link = UsableLink(post);
        var text = TextRules.Assemble(post.Text, link, link?.AbsoluteUri, TextRules.NormalizeTags(post.Tags));
        var length = TextRules.MastodonLength(text, _account.CharactersReservedPerUrl);
        var problems = new List<string>(LinkProblems(post));
        problems.AddRange(CardProblems(post));
        problems.AddRange(ImageProblems(post, new MediaRules(_account.MaxMediaAttachments, _account.ImageSizeLimit, MaxDescription,
            AnimatedGifRule.Alone, _account.VideoSizeLimit)));
        if (length > _account.MaxCharacters) problems.Add($"The post is {length} characters and this instance takes {_account.MaxCharacters}.");
        if (text.Length == 0 && post.Images.Count == 0) problems.Add("The post is empty.");
        return new SocialComposition(text, length, _account.MaxCharacters, "characters", problems);
    }

    protected override async Task<SocialPostResult> SendComposedAsync(SocialPost post, SocialComposition composed, SocialAttempt attempt, CancellationToken ct)
    {
        if (attempt.Resuming && !attempt.ResendUnconfirmed && Now - attempt.StartedAt > Window)
            return SocialPostResult.Failed(SocialFailureKind.Unconfirmed,
                $"An earlier try began {attempt.StartedAt:u}, past the hour Mastodon remembers its key: check the account before sending again.");

        var uploaded = new List<string>();
        for (var i = 0; i < post.Images.Count; i++)
        {
            var (mediaId, uploadFailure) = await UploadAsync(post.Images[i], uploaded, ct).ConfigureAwait(false);
            if (mediaId is not null)
            {
                uploaded.Add(mediaId);
                continue;
            }
            var left = await DeleteAsync(uploaded, ct).ConfigureAwait(false);
            return SocialPostResult.Failed(uploadFailure! with
            {
                Message = Invariant($"Picture {i + 1} of {post.Images.Count} didn't upload ({uploadFailure!.Message}). Nothing was posted; ")
                    + Leftovers(uploaded.Count, left),
                MayHavePosted = false,
            });
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("status", composed.Text),
            new("visibility", _account.Visibility),
        };
        if (!string.IsNullOrWhiteSpace(post.Language)) fields.Add(new("language", post.Language));
        fields.AddRange(uploaded.Select(id => new KeyValuePair<string, string>("media_ids[]", id)));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_instance, "/api/v1/statuses")) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Authorization = Bearer(_account.AccessToken);
        request.Headers.Add("Idempotency-Key", attempt.Token);
        using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!IsSuccess(response))
        {
            var failure = Failure(response, doc);
            // Refused outright: the status doesn't exist, so the pictures uploaded for it are orphans. When it may exist,
            // they stay: they may be its pictures.
            if (!failure.MayHavePosted && uploaded.Count > 0)
            {
                var left = await DeleteAsync(uploaded, ct).ConfigureAwait(false);
                failure = failure with { Message = Clean(failure.Message + " Nothing was posted; " + Leftovers(uploaded.Count, left)) };
            }
            return SocialPostResult.Failed(failure);
        }
        if (doc is null || Str(doc.RootElement, "id") is not { } id)
            return SocialPostResult.Failed(SocialFailureKind.Transient, "Mastodon's answer had no status id.", mayHavePosted: true);

        // A resend under the same key answers with the first status, which carries the first try's pictures: this try's
        // uploads are then unattached, so they go.
        if (uploaded.Count > 0)
        {
            var attached = Prop(doc.RootElement, "media_attachments") is { ValueKind: JsonValueKind.Array } media
                ? media.EnumerateArray().Select(m => Str(m, "id")).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
            await DeleteAsync([.. uploaded.Where(m => !attached.Contains(m))], ct).ConfigureAwait(false);
        }
        var url = Str(doc.RootElement, "url") is { } u && Uri.TryCreate(u, UriKind.Absolute, out var parsed) ? parsed : null;
        return SocialPostResult.Success(id, url);
    }

    /// <summary>
    /// One picture: <c>POST /api/v2/media</c> with its alt text; a 202 is still processing, so <c>GET /api/v1/media/:id</c>
    /// is polled (206 while processing, 200 when done, 422 when processing failed) until it can be attached.
    /// </summary>
    private async Task<(string? Id, SocialFailure? Failure)> UploadAsync(SocialImage image, List<string> uploaded, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var type = NormalType(image);
        var file = new ReadOnlyMemoryContent(image.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", "picture." + Extension(type));
        form.Add(new StringContent(image.Alt), "description");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_instance, "/api/v2/media")) { Content = form };
        request.Headers.Authorization = Bearer(_account.AccessToken);
        using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!IsSuccess(response)) return (null, Failure(response, doc) with { MayHavePosted = false });
        if (doc is null || Str(doc.RootElement, "id") is not { } id)
            return (null, new SocialFailure(SocialFailureKind.Transient, "Mastodon's answer had no media id."));
        if (response.StatusCode != HttpStatusCode.Accepted) return (id, null);

        for (var poll = 0; poll < _account.MediaPollAttempts; poll++)
        {
            await Task.Delay(_account.MediaPollInterval, ct).ConfigureAwait(false);
            using var check = new HttpRequestMessage(HttpMethod.Get, new Uri(_instance, "/api/v1/media/" + Uri.EscapeDataString(id)));
            check.Headers.Authorization = Bearer(_account.AccessToken);
            using var state = await Api.SendAsync(check, ct).ConfigureAwait(false);
            if (state.StatusCode == HttpStatusCode.OK) return (id, null);
            if (state.StatusCode == HttpStatusCode.PartialContent) continue;
            using var stateDoc = await ReadJsonAsync(state, ct).ConfigureAwait(false);
            uploaded.Add(id);   // it exists on the instance, so it is cleaned up with the others
            var said = stateDoc is null ? null : Str(stateDoc.RootElement, "error");
            return (null, state.StatusCode == HttpStatusCode.UnprocessableEntity
                ? new SocialFailure(SocialFailureKind.Rejected, Clean("Mastodon couldn't process it: " + (said ?? "422")))
                : Failure(state, stateDoc) with { MayHavePosted = false });
        }
        uploaded.Add(id);
        return (null, new SocialFailure(SocialFailureKind.Transient, Invariant($"Mastodon was still processing it after {_account.MediaPollAttempts} checks")));
    }

    /// <summary>
    /// Deletes unattached pictures (<c>DELETE /api/v1/media/:id</c>, Mastodon 4.4 and later); returns how many are left.
    /// An older instance doesn't have the call and keeps them unattached.
    /// </summary>
    private async Task<int> DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var left = 0;
        foreach (var id in ids)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_instance, "/api/v1/media/" + Uri.EscapeDataString(id)));
                request.Headers.Authorization = Bearer(_account.AccessToken);
                using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
                if (!IsSuccess(response)) left++;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                left++;
            }
        }
        return left;
    }

    private static string Leftovers(int uploaded, int left) => uploaded == 0
        ? "no picture had been uploaded yet."
        : left == 0
            ? Invariant($"the {uploaded} already uploaded were deleted.")
            : Invariant($"{left} of the {uploaded} already uploaded couldn't be deleted and stay unattached on the instance, where no one sees them.");

    private SocialFailure Failure(HttpResponseMessage response, JsonDocument? doc) =>
        FromStatus(response, doc is null ? null : Str(doc.RootElement, "error"));
}
