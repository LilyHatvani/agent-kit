namespace AgentKit.Social.Internal;

/// <summary>
/// A Facebook Page through the Graph API with a Page access token (https://developers.facebook.com/docs/pages-api/posts).
/// <list type="bullet">
/// <item>Post: <c>POST https://graph.facebook.com/{version}/{page}/feed</c> with <c>message</c> and <c>link</c>; Facebook
/// builds the link's preview from the page. Answered with <c>id</c>, <c>{page}_{post}</c>.</item>
/// <item>Pictures: not uploaded (Facebook shows the link's own picture); a note says so.</item>
/// <item>Address: <c>permalink_url</c>, read after posting (best effort), else <c>https://www.facebook.com/{id}</c>.</item>
/// <item>Only Pages: Facebook has no API to post on a person's own profile.</item>
/// <item>Retry: no key (<see cref="MetaChannelBase"/>); Facebook refuses an immediate duplicate (506).</item>
/// </list>
/// </summary>
internal sealed class FacebookPageChannel(SocialChannelContext context, FacebookPage page) : MetaChannelBase(context, [page.AccessToken])
{
    /// <summary>The longest message Facebook takes.</summary>
    public const int MaxLength = 63_206;

    private readonly FacebookPage _page = page;

    public override string Network => SocialNetworks.Facebook;

    public override string Id => $"facebook:{_page.PageId}";

    private string Api => $"https://graph.facebook.com/{_page.GraphVersion}";

    public override SocialComposition Compose(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        var text = TextRules.Assemble(post.Text, null, null, TextRules.NormalizeTags(post.Tags));
        var length = TextRules.Graphemes(text);
        var problems = new List<string>(LinkProblems(post));
        if (length > MaxLength) problems.Add($"The post is {length} characters and Facebook takes {MaxLength}.");
        if (text.Length == 0 && UsableLink(post) is null) problems.Add("The post is empty.");
        return new SocialComposition(text, length, MaxLength, "characters", problems);
    }

    protected override async Task<SocialPostResult> SendComposedAsync(SocialPost post, SocialComposition composed, SocialAttempt attempt, CancellationToken ct)
    {
        if (Unconfirmed(attempt) is { } unconfirmed) return unconfirmed;
        var notes = new List<string>();
        if (post.Images.Count > 0) notes.Add("Facebook shows the link's own picture, so the post's pictures weren't uploaded.");

        var fields = new Dictionary<string, string>();
        if (composed.Text.Length > 0) fields["message"] = composed.Text;
        if (UsableLink(post) is { } link) fields["link"] = link.AbsoluteUri;
        using var request = Authorized(HttpMethod.Post, $"{Api}/{Uri.EscapeDataString(_page.PageId)}/feed", fields);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!IsSuccess(response)) return SocialPostResult.Failed(GraphFailure(response, doc));
        if (doc is null || Str(doc.RootElement, "id") is not { } id)
            return SocialPostResult.Failed(SocialFailureKind.Transient, "Facebook's answer had no post id.", mayHavePosted: true);

        Uri url = new($"https://www.facebook.com/{Uri.EscapeDataString(id)}");
        try
        {
            using var after = AfterPost();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, after.Token);
            using var read = Authorized(HttpMethod.Get, $"{Api}/{Uri.EscapeDataString(id)}?fields=permalink_url", null);
            using var readResponse = await SendAsync(read, linked.Token).ConfigureAwait(false);
            using var readDoc = await ReadJsonAsync(readResponse, linked.Token).ConfigureAwait(false);
            if (IsSuccess(readResponse) && readDoc is not null && Str(readDoc.RootElement, "permalink_url") is { } permalink
                && Uri.TryCreate(permalink, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
                url = u;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
        }
        return SocialPostResult.Success(id, url, notes);
    }

    /// <summary>
    /// A request carrying the token as Meta documents it, the <c>access_token</c> parameter: in the form for a POST, in
    /// the query for a GET. The API client logs no address, and a message that echoes the token is scrubbed.
    /// </summary>
    private HttpRequestMessage Authorized(HttpMethod method, string url, Dictionary<string, string>? form)
    {
        if (form is null)
            return new HttpRequestMessage(method, url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "access_token=" + Uri.EscapeDataString(_page.AccessToken));
        form["access_token"] = _page.AccessToken;
        return new HttpRequestMessage(method, url) { Content = new FormUrlEncodedContent(form) };
    }
}
