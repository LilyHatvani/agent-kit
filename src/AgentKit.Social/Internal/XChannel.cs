using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentKit.Social.Internal;

/// <summary>
/// X through API v2 with an OAuth 2.0 user access token.
/// <list type="bullet">
/// <item>Post: <c>POST https://api.x.com/2/tweets</c> with <c>{ "text", "media": { "media_ids" } }</c>, answered 201 with
/// <c>data.id</c> (https://docs.x.com/x-api/posts/create-post).</item>
/// <item>Pictures: <c>POST https://api.x.com/2/media/upload</c>, multipart <c>media</c> and <c>media_category</c>
/// (<c>tweet_image</c>, or <c>tweet_gif</c> for an animated GIF), scope <c>media.write</c>
/// (https://docs.x.com/x-api/media/upload-media); while <c>processing_info.state</c> is pending or in progress,
/// <c>GET /2/media/upload?command=STATUS&amp;media_id=</c> (https://docs.x.com/x-api/media/get-media-upload-status); alt
/// text by <c>POST /2/media/metadata</c>, at most 1000 characters (https://docs.x.com/x-api/media/create-media-metadata).
/// Up to 4 pictures of at most 5 MB, or one animated GIF of at most 15 MB
/// (https://docs.x.com/x-api/media/quickstart/best-practices).</item>
/// <item>Length: 280 weighted characters, URLs 23 each (https://docs.x.com/resources/fundamentals/counting-characters).</item>
/// <item>Card: X builds it from the page's tags when the post has no pictures.</item>
/// <item>Retry: nothing to check by key. X itself refuses a post whose text repeats a recent one (403), which a person's
/// resend meets as a rejection rather than a duplicate.</item>
/// <item>Errors: problem details <c>{ title, detail, type }</c> or <c>{ errors: [{ message }] }</c>; rate limits in
/// <c>x-rate-limit-reset</c>, Unix seconds (https://docs.x.com/x-api/fundamentals/rate-limits).</item>
/// </list>
/// </summary>
internal sealed partial class XChannel : ChannelBase
{
    public const string TokenEndpoint = "https://api.x.com/2/oauth2/token";
    private const string PostEndpoint = "https://api.x.com/2/tweets";
    private const string UploadEndpoint = "https://api.x.com/2/media/upload";
    private const string MetadataEndpoint = "https://api.x.com/2/media/metadata";

    private static readonly MediaRules Pictures = new(4, 5_000_000, 1000, AnimatedGifRule.Alone, 15_000_000);

    private readonly XAccount _account;

    public XChannel(SocialChannelContext context, XAccount account, IEnumerable<string?>? moreSecrets = null)
        : base(context, [account.AccessToken, account.ApiKey, account.ApiKeySecret, account.AccessTokenSecret, .. moreSecrets ?? []])
    {
        _account = account;
        MediaPolling.Validate(account.MediaPollInterval, account.MediaPollAttempts, nameof(account));
    }

    public override string Network => SocialNetworks.X;

    public override string Id => "x";

    public override SocialIdempotency Idempotency { get; } = new(SocialIdempotencyKind.None, null,
        "X can't be asked by key what was posted: a retry after a lost answer needs a person to check the account.");

    public override SocialComposition Compose(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        var link = UsableLink(post);
        var text = TextRules.Assemble(post.Text, link, link?.AbsoluteUri, TextRules.NormalizeTags(post.Tags));
        var length = TextRules.XWeightedLength(text);
        var problems = new List<string>(LinkProblems(post));
        problems.AddRange(CardProblems(post));
        problems.AddRange(ImageProblems(post, Pictures));
        if (length > _account.MaxWeightedLength) problems.Add($"The post weighs {length} characters and X takes {_account.MaxWeightedLength}.");
        if (text.Length == 0 && post.Images.Count == 0) problems.Add("The post is empty.");
        return new SocialComposition(text, length, _account.MaxWeightedLength, "weighted characters", problems);
    }

    protected override async Task<SocialPostResult> SendComposedAsync(SocialPost post, SocialComposition composed, SocialAttempt attempt, CancellationToken ct)
    {
        if (attempt.Resuming && !attempt.ResendUnconfirmed)
            return SocialPostResult.Failed(SocialFailureKind.Unconfirmed,
                $"An earlier try began {attempt.StartedAt:u} and X can't be asked whether it posted: check the account before sending again.");

        var notes = new List<string>();
        var mediaIds = new JsonArray();
        for (var i = 0; i < post.Images.Count; i++)
        {
            string? mediaId;
            SocialFailure? failure;
            try
            {
                (mediaId, failure) = await UploadAsync(post.Images[i], i + 1, notes, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A transport failure on an upload: the post itself was never sent, so a retry starts afresh.
                (mediaId, failure) = (null, FromException(e));
            }
            if (mediaId is not null)
            {
                mediaIds.Add(mediaId);
                continue;
            }
            return SocialPostResult.Failed(failure! with
            {
                Message = Invariant($"Picture {i + 1} of {post.Images.Count} didn't upload ({failure!.Message}). Nothing was posted; ")
                    + (i == 0 ? "no picture had been uploaded yet." : "X has no way to delete an upload, so the ones already uploaded expire unused."),
                MayHavePosted = false,
            });
        }

        var body = new JsonObject { ["text"] = composed.Text };
        if (mediaIds.Count > 0) body["media"] = new JsonObject { ["media_ids"] = mediaIds };
        using var request = new HttpRequestMessage(HttpMethod.Post, PostEndpoint) { Content = JsonContent.Create(body) };
        Authorize(request);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!IsSuccess(response)) return SocialPostResult.Failed(Failure(response, doc));
        if (doc is null || Prop(doc.RootElement, "data") is not { } data || Str(data, "id") is not { } id)
            return SocialPostResult.Failed(SocialFailureKind.Transient, "X's answer had no post id.", mayHavePosted: true);
        return SocialPostResult.Success(id, new Uri($"https://x.com/i/web/status/{Uri.EscapeDataString(id)}"), notes);
    }

    /// <summary>One picture: upload, wait while X processes it, then its alt text.</summary>
    private async Task<(string? Id, SocialFailure? Failure)> UploadAsync(SocialImage image, int number, List<string> notes, CancellationToken ct)
    {
        var type = NormalType(image);
        var category = type == "image/gif" && Media.IsAnimatedGif(image.Bytes.Span) ? "tweet_gif" : "tweet_image";
        using var form = new MultipartFormDataContent();
        var file = new ReadOnlyMemoryContent(image.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "media", "picture." + Extension(type));
        form.Add(new StringContent(category), "media_category");
        using var request = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint) { Content = form };
        Authorize(request);
        using var response = await SendAsync(request, ct, SocialDefaults.UploadTimeout(image.Bytes.Length)).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!IsSuccess(response)) return (null, Failure(response, doc) with { MayHavePosted = false });
        // The spec's media id is 1 to 19 digits; anything else can't go into media_ids.
        if (doc is null || Prop(doc.RootElement, "data") is not { } data || Str(data, "id") is not { } id || !MediaId().IsMatch(id))
            return (null, new SocialFailure(SocialFailureKind.Transient, "X's answer had no media id it could read."));

        var state = Prop(data, "processing_info") is { } info ? Str(info, "state") : null;
        for (var poll = 0; state is "pending" or "in_progress"; poll++)
        {
            if (poll >= _account.MediaPollAttempts)
                return (null, new SocialFailure(SocialFailureKind.Transient, Invariant($"X was still processing it after {_account.MediaPollAttempts} checks")));
            await Task.Delay(_account.MediaPollInterval, ct).ConfigureAwait(false);
            using var check = new HttpRequestMessage(HttpMethod.Get, $"{UploadEndpoint}?command=STATUS&media_id={Uri.EscapeDataString(id)}");
            Authorize(check);
            using var checkResponse = await SendAsync(check, ct).ConfigureAwait(false);
            using var checkDoc = await ReadJsonAsync(checkResponse, ct).ConfigureAwait(false);
            if (!IsSuccess(checkResponse)) return (null, Failure(checkResponse, checkDoc) with { MayHavePosted = false });
            state = checkDoc is not null && Prop(checkDoc.RootElement, "data") is { } d && Prop(d, "processing_info") is { } i ? Str(i, "state") : "succeeded";
        }
        if (state == "failed") return (null, new SocialFailure(SocialFailureKind.Rejected, "X couldn't process it"));

        using var alt = new HttpRequestMessage(HttpMethod.Post, MetadataEndpoint)
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["id"] = id,
                ["metadata"] = new JsonObject { ["alt_text"] = new JsonObject { ["text"] = image.Alt } },
            }),
        };
        Authorize(alt);
        // The picture is up: a refused or unanswered description costs the description, not the picture (as Echo of
        // Dreams' announcement poster does), and the result says so.
        try
        {
            using var altResponse = await SendAsync(alt, ct).ConfigureAwait(false);
            if (!IsSuccess(altResponse))
            {
                using var altDoc = await ReadJsonAsync(altResponse, ct).ConfigureAwait(false);
                notes.Add(Invariant($"Picture {number} went without its alt text: {Failure(altResponse, altDoc).Message}"));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            notes.Add(Invariant($"Picture {number} went without its alt text: {FromException(e).Message}"));
        }
        return (id, null);
    }

    /// <summary>OAuth 1.0a: a fresh signature per request (the query is signed, a JSON or multipart body is not); OAuth 2.0: the bearer.</summary>
    private void Authorize(HttpRequestMessage request)
    {
        if (_account.IsOAuth1)
        {
            var credentials = new OAuth1Signer.Credentials(_account.ApiKey!, _account.ApiKeySecret!, _account.AccessToken, _account.AccessTokenSecret!);
            request.Headers.TryAddWithoutValidation("Authorization", OAuth1Signer.AuthorizationHeader(
                request.Method, request.RequestUri!, [], credentials, OAuth1Signer.NewNonce(), Now.ToUnixTimeSeconds()));
        }
        else
        {
            request.Headers.Authorization = Bearer(_account.AccessToken);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\A[0-9]{1,19}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MediaId();

    internal async Task<XTokenRefresh> RefreshAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (!IsSuccess(response))
            {
                var failure = Failure(response, doc);
                // OAuth's invalid_grant (400): the refresh token is spent or revoked; only connecting X again helps.
                if (doc is not null && Str(doc.RootElement, "error") is "invalid_grant" or "invalid_client" or "unauthorized_client")
                    failure = failure with { Kind = SocialFailureKind.Auth };
                return new XTokenRefresh(null, failure);
            }
            if (doc is null || Str(doc.RootElement, "access_token") is not { } access)
                return new XTokenRefresh(null, new SocialFailure(SocialFailureKind.Transient, "X's token answer had no access token."));
            AddSecret(access);
            var refresh = Str(doc.RootElement, "refresh_token");
            AddSecret(refresh);
            DateTimeOffset? expires = Number(doc.RootElement, "expires_in") is { } seconds && seconds is > 0 and < 100_000_000
                ? Now.AddSeconds(seconds)
                : null;
            return new XTokenRefresh(new XTokens(access, refresh, expires), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new XTokenRefresh(null, FromException(e));
        }
    }

    private SocialFailure Failure(HttpResponseMessage response, JsonDocument? doc)
    {
        string? message = null;
        if (doc is not null)
        {
            var root = doc.RootElement;
            message = Str(root, "detail") ?? Str(root, "error_description") ?? Str(root, "title") ?? Str(root, "error");
            if (message is null && Prop(root, "errors") is { ValueKind: JsonValueKind.Array } errors && errors.GetArrayLength() > 0)
                message = Str(errors[0], "message");
        }
        var failure = FromStatus(response, message);
        // A 403 on posting is X refusing this post (a duplicate, a policy) or this app's access tier, not a dead token:
        // its message says which.
        return response.StatusCode == HttpStatusCode.Forbidden ? failure with { Kind = SocialFailureKind.Rejected } : failure;
    }
}
