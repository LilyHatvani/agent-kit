using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentKit.Social.Internal;

/// <summary>
/// X through API v2 with an OAuth 2.0 user access token.
/// <list type="bullet">
/// <item>Post: <c>POST https://api.x.com/2/tweets</c> with <c>{ "text": … }</c>, answered 201 with <c>data.id</c>
/// (https://docs.x.com/x-api/posts/create-post).</item>
/// <item>Length: 280 weighted characters, URLs 23 each (https://docs.x.com/resources/fundamentals/counting-characters).</item>
/// <item>Card: X builds it from the page's tags; no media is uploaded.</item>
/// <item>Retry: nothing to check by key. X itself refuses a post whose text repeats a recent one (403), which a person's
/// resend meets as a rejection rather than a duplicate.</item>
/// <item>Errors: problem details <c>{ title, detail, type }</c> or <c>{ errors: [{ message }] }</c>; rate limits in
/// <c>x-rate-limit-reset</c>, Unix seconds (https://docs.x.com/x-api/fundamentals/rate-limits).</item>
/// </list>
/// </summary>
internal sealed class XChannel : ChannelBase
{
    public const string TokenEndpoint = "https://api.x.com/2/oauth2/token";
    private const string PostEndpoint = "https://api.x.com/2/tweets";

    private readonly XAccount _account;

    public XChannel(SocialChannelContext context, XAccount account, IEnumerable<string?>? moreSecrets = null)
        : base(context, [account.AccessToken, .. moreSecrets ?? []])
    {
        _account = account;
    }

    public override string Network => SocialNetworks.X;

    public override string Id => "x";

    public override SocialIdempotency Idempotency { get; } = new(SocialIdempotencyKind.None, null,
        "X can't be asked by key what was posted: a retry after a lost answer needs a person to check the account.");

    public override SocialComposition Compose(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        var text = TextRules.Assemble(post.Text, post.Link, post.Link?.AbsoluteUri, TextRules.NormalizeTags(post.Tags));
        var length = TextRules.XWeightedLength(text);
        var problems = new List<string>(CardProblems(post));
        if (length > _account.MaxWeightedLength) problems.Add($"The post weighs {length} characters and X takes {_account.MaxWeightedLength}.");
        if (text.Length == 0) problems.Add("The post is empty.");
        return new SocialComposition(text, length, _account.MaxWeightedLength, "weighted characters", problems);
    }

    protected override async Task<SocialPostResult> SendComposedAsync(SocialPost post, SocialComposition composed, SocialAttempt attempt, CancellationToken ct)
    {
        if (attempt.Resuming && !attempt.ResendUnconfirmed)
            return SocialPostResult.Failed(SocialFailureKind.Unconfirmed,
                $"An earlier try began {attempt.StartedAt:u} and X can't be asked whether it posted: check the account before sending again.");

        using var request = new HttpRequestMessage(HttpMethod.Post, PostEndpoint) { Content = JsonContent.Create(new JsonObject { ["text"] = composed.Text }) };
        request.Headers.Authorization = Bearer(_account.AccessToken);
        using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!IsSuccess(response)) return SocialPostResult.Failed(Failure(response, doc));
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || Str(data, "id") is not { } id)
            return SocialPostResult.Failed(SocialFailureKind.Transient, "X's answer had no post id.", mayHavePosted: true);
        return SocialPostResult.Success(id, new Uri($"https://x.com/i/web/status/{id}"));
    }

    internal async Task<XTokenRefresh> RefreshAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
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
            DateTimeOffset? expires = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds)
                ? Now.AddSeconds(seconds)
                : null;
            return new XTokenRefresh(new XTokens(access, Str(doc.RootElement, "refresh_token"), expires), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
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
            if (message is null && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                message = Str(errors[0], "message");
        }
        var failure = FromStatus(response, message);
        // A 403 on posting is X refusing this post (a duplicate, a policy) or this app's access tier, not a dead token:
        // its message says which.
        return response.StatusCode == HttpStatusCode.Forbidden ? failure with { Kind = SocialFailureKind.Rejected } : failure;
    }
}
