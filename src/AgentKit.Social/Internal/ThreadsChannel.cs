using System.Globalization;
using System.Text;

namespace AgentKit.Social.Internal;

/// <summary>
/// Threads through Meta's Threads API with a long-lived user token (https://developers.facebook.com/docs/threads/posts).
/// <list type="bullet">
/// <item>Post: a container, <c>POST {host}/v1.0/{user}/threads</c> with <c>media_type=TEXT</c>, <c>text</c> and
/// <c>link_attachment</c> (the link's preview), then <c>POST {host}/v1.0/{user}/threads_publish</c> with
/// <c>creation_id</c>. Between them the container's <c>status</c> is read until <c>FINISHED</c>
/// (https://developers.facebook.com/docs/threads/troubleshooting).</item>
/// <item>Length: 500 characters, an emoji counted as its UTF-8 bytes (https://developers.facebook.com/docs/threads/overview).
/// The link goes in <c>link_attachment</c>, not the text.</item>
/// <item>Pictures: Threads takes them only by a public address, never as bytes, so a post with pictures goes as a link
/// post and a note says so.</item>
/// <item>Address: the post's <c>permalink</c>, read after publishing (best effort).</item>
/// <item>Retry: no key (<see cref="MetaChannelBase"/>).</item>
/// </list>
/// </summary>
internal sealed class ThreadsChannel(SocialChannelContext context, ThreadsAccount account) : MetaChannelBase(context, [account.AccessToken])
{
    public const int MaxLength = 500;

    private readonly ThreadsAccount _account = account;

    public override string Network => SocialNetworks.Threads;

    public override string Id => $"threads:{_account.UserId}";

    private string Api => $"{_account.Host.AbsoluteUri.TrimEnd('/')}/v1.0";

    public override SocialComposition Compose(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        // Hashtags aren't topics on Threads (one topic per post, set apart from the text): the words go alone.
        var text = TextRules.Assemble(post.Text, null, null, [], includeTags: false);
        var length = Length(text);
        var problems = new List<string>(LinkProblems(post));
        if (length > MaxLength) problems.Add($"The post is {length} characters and Threads takes {MaxLength}.");
        if (text.Length == 0 && UsableLink(post) is null) problems.Add("The post is empty.");
        return new SocialComposition(text, length, MaxLength, "characters", problems);
    }

    /// <summary>Threads' count: one per character, an emoji (a symbol, or anything outside the basic plane) as its UTF-8 bytes.</summary>
    internal static int Length(string text)
    {
        var n = 0;
        foreach (var rune in text.EnumerateRunes())
            n += rune.Value > 0xFFFF || Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherSymbol ? rune.Utf8SequenceLength : 1;
        return n;
    }

    protected override async Task<SocialPostResult> SendComposedAsync(SocialPost post, SocialComposition composed, SocialAttempt attempt, CancellationToken ct)
    {
        if (Unconfirmed(attempt) is { } unconfirmed) return unconfirmed;
        var notes = new List<string>();
        if (post.Images.Count > 0) notes.Add("Threads takes pictures only by a public address, so it went as a link post without them.");

        var fields = new Dictionary<string, string> { ["media_type"] = "TEXT", ["text"] = composed.Text };
        if (UsableLink(post) is { } link) fields["link_attachment"] = link.AbsoluteUri;
        using (var create = Authorized(HttpMethod.Post, $"{Api}/{Uri.EscapeDataString(_account.UserId)}/threads", fields))
        using (var response = await SendAsync(create, ct).ConfigureAwait(false))
        using (var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false))
        {
            // Nothing is public until the publish call: a failed container posted nothing.
            if (!IsSuccess(response)) return SocialPostResult.Failed(GraphFailure(response, doc) with { MayHavePosted = false });
            if (doc is null || Str(doc.RootElement, "id") is not { } containerId)
                return SocialPostResult.Failed(SocialFailureKind.Transient, "Threads' answer had no container id.");
            if (await WaitForContainerAsync(containerId, ct).ConfigureAwait(false) is { } notReady) return SocialPostResult.Failed(notReady);
            return await PublishAsync(containerId, notes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the container until Threads says it is ready; a container that failed or expired posted nothing.</summary>
    private async Task<SocialFailure?> WaitForContainerAsync(string containerId, CancellationToken ct)
    {
        for (var check = 0; check < _account.ContainerPollAttempts; check++)
        {
            using var request = Authorized(HttpMethod.Get, $"{Api}/{Uri.EscapeDataString(containerId)}?fields=status,error_message", null);
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (!IsSuccess(response)) return GraphFailure(response, doc) with { MayHavePosted = false };
            var status = doc is null ? null : Str(doc.RootElement, "status");
            switch (status)
            {
                case "FINISHED":
                case null: // an answer without a status: try to publish, Threads says there if it isn't ready
                    return null;
                case "ERROR":
                case "EXPIRED":
                    return new SocialFailure(SocialFailureKind.Rejected,
                        Clean($"Threads couldn't prepare the post ({status}): {(doc is null ? "" : Str(doc.RootElement, "error_message"))}"));
            }
            await Task.Delay(_account.ContainerPollInterval, Context.Clock, ct).ConfigureAwait(false);
        }
        return new SocialFailure(SocialFailureKind.Transient, Invariant($"Threads was still preparing the post after {_account.ContainerPollAttempts} checks; nothing was published."));
    }

    private async Task<SocialPostResult> PublishAsync(string containerId, List<string> notes, CancellationToken ct)
    {
        using var publish = Authorized(HttpMethod.Post, $"{Api}/{Uri.EscapeDataString(_account.UserId)}/threads_publish",
            new Dictionary<string, string> { ["creation_id"] = containerId });
        using var response = await SendAsync(publish, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!IsSuccess(response)) return SocialPostResult.Failed(GraphFailure(response, doc));
        if (doc is null || Str(doc.RootElement, "id") is not { } mediaId)
            return SocialPostResult.Failed(SocialFailureKind.Transient, "Threads' answer had no post id.", mayHavePosted: true);

        // The post is up: its address is a follow-up that may fail without unposting it.
        Uri? url = null;
        try
        {
            using var after = AfterPost();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, after.Token);
            using var read = Authorized(HttpMethod.Get, $"{Api}/{Uri.EscapeDataString(mediaId)}?fields=permalink", null);
            using var readResponse = await SendAsync(read, linked.Token).ConfigureAwait(false);
            using var readDoc = await ReadJsonAsync(readResponse, linked.Token).ConfigureAwait(false);
            if (IsSuccess(readResponse) && readDoc is not null && Str(readDoc.RootElement, "permalink") is { } permalink
                && Uri.TryCreate(permalink, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
                url = u;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
        }
        if (url is null) notes.Add("Threads didn't give the post's address.");
        return SocialPostResult.Success(mediaId, url, notes);
    }

    /// <summary>
    /// A request carrying the token as Meta documents it, the <c>access_token</c> parameter: in the form for a POST, in
    /// the query for a GET. The API client logs no address, and a message that echoes the token is scrubbed.
    /// </summary>
    private HttpRequestMessage Authorized(HttpMethod method, string url, Dictionary<string, string>? form)
    {
        if (form is null)
            return new HttpRequestMessage(method, url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "access_token=" + Uri.EscapeDataString(_account.AccessToken));
        form["access_token"] = _account.AccessToken;
        return new HttpRequestMessage(method, url) { Content = new FormUrlEncodedContent(form) };
    }

    /// <summary>Trades a long-lived token for a fresh one (60 more days); the token must be at least a day old and not expired.</summary>
    internal async Task<ThreadsTokenRefresh> RefreshAsync(CancellationToken ct)
    {
        var url = $"{_account.Host.AbsoluteUri.TrimEnd('/')}/refresh_access_token?grant_type=th_refresh_token&access_token={Uri.EscapeDataString(_account.AccessToken)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (!IsSuccess(response)) return new ThreadsTokenRefresh(null, GraphFailure(response, doc) with { MayHavePosted = false });
            if (doc is null || Str(doc.RootElement, "access_token") is not { } token)
                return new ThreadsTokenRefresh(null, new SocialFailure(SocialFailureKind.Transient, "Threads' token answer had no access token."));
            AddSecret(token);
            DateTimeOffset? expires = Number(doc.RootElement, "expires_in") is { } seconds && seconds is > 0 and < 100_000_000
                ? Now.AddSeconds(seconds)
                : null;
            return new ThreadsTokenRefresh(new ThreadsToken(token, expires), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new ThreadsTokenRefresh(null, FromException(e) with { MayHavePosted = false });
        }
    }
}
