using System.Net;
using System.Net.Http.Headers;

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

    private const int MaxMedia = 10_000_000;

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
        var text = TextRules.Assemble(post.Text, post.Link, post.Link?.AbsoluteUri, TextRules.NormalizeTags(post.Tags));
        var length = TextRules.MastodonLength(text, _account.CharactersReservedPerUrl);
        var problems = new List<string>(CardProblems(post));
        if (length > _account.MaxCharacters) problems.Add($"The post is {length} characters and this instance takes {_account.MaxCharacters}.");
        if (text.Length == 0) problems.Add("The post is empty.");
        return new SocialComposition(text, length, _account.MaxCharacters, "characters", problems);
    }

    protected override async Task<SocialPostResult> SendComposedAsync(SocialPost post, SocialComposition composed, SocialAttempt attempt, CancellationToken ct)
    {
        if (attempt.Resuming && !attempt.ResendUnconfirmed && Now - attempt.StartedAt > Window)
            return SocialPostResult.Failed(SocialFailureKind.Unconfirmed,
                $"An earlier try began {attempt.StartedAt:u}, past the hour Mastodon remembers its key: check the account before sending again.");

        var notes = new List<string>();
        var fields = new List<KeyValuePair<string, string>>
        {
            new("status", composed.Text),
            new("visibility", _account.Visibility),
        };
        if (!string.IsNullOrWhiteSpace(post.Language)) fields.Add(new("language", post.Language));
        if (_account.AttachCardImage && await UploadCardAsync(post.Card, notes, ct).ConfigureAwait(false) is { } mediaId)
            fields.Add(new("media_ids[]", mediaId));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_instance, "/api/v1/statuses")) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Authorization = Bearer(_account.AccessToken);
        request.Headers.Add("Idempotency-Key", attempt.Token);
        using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!IsSuccess(response)) return SocialPostResult.Failed(Failure(response, doc));
        if (doc is null || Str(doc.RootElement, "id") is not { } id)
            return SocialPostResult.Failed(SocialFailureKind.Transient, "Mastodon's answer had no status id.", mayHavePosted: true);
        var url = Str(doc.RootElement, "url") is { } u && Uri.TryCreate(u, UriKind.Absolute, out var parsed) ? parsed : null;
        return SocialPostResult.Success(id, url, notes, alreadyPosted: false);
    }

    private async Task<string?> UploadCardAsync(LinkCard? card, List<string> notes, CancellationToken ct)
    {
        if (await LoadCardImageAsync(card, MaxMedia, notes, ct).ConfigureAwait(false) is not { } image) return null;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        form.Add(file, "file", "card." + Extension(image.ContentType));
        if (!string.IsNullOrWhiteSpace(card!.ImageAlt)) form.Add(new StringContent(card.ImageAlt), "description");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_instance, "/api/v2/media")) { Content = form };
        request.Headers.Authorization = Bearer(_account.AccessToken);
        using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            notes.Add("Mastodon was still processing the card picture, so the status went without it.");
            return null;
        }
        if (!IsSuccess(response) || doc is null || Str(doc.RootElement, "id") is not { } id)
        {
            notes.Add($"The card picture didn't upload ({Failure(response, doc).Message}), so the status went without it.");
            return null;
        }
        return id;
    }

    private SocialFailure Failure(HttpResponseMessage response, System.Text.Json.JsonDocument? doc) =>
        FromStatus(response, doc is null ? null : Str(doc.RootElement, "error"));
}
