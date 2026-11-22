using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentKit.Social.Internal;

/// <summary>
/// Bluesky over the AT Protocol's HTTP API (XRPC), with an app password.
/// <list type="bullet">
/// <item>Session: <c>com.atproto.server.createSession</c> (https://github.com/bluesky-social/atproto/blob/main/lexicons/com/atproto/server/createSession.json),
/// kept in memory per account and made again on <c>ExpiredToken</c>.</item>
/// <item>Post: <c>com.atproto.repo.createRecord</c> of an <c>app.bsky.feed.post</c> with an rkey chosen before sending
/// (https://github.com/bluesky-social/atproto/blob/main/lexicons/com/atproto/repo/createRecord.json), text at most 300 graphemes and 3000 UTF-8 bytes,
/// link and tag facets indexed in UTF-8 bytes (https://github.com/bluesky-social/atproto/blob/main/packages/pds/lexicons/app/bsky/richtext/facet.json).</item>
/// <item>Card: an <c>app.bsky.embed.external</c> whose thumb is a blob from <c>com.atproto.repo.uploadBlob</c>, at most
/// 1,000,000 bytes (https://github.com/bluesky-social/atproto/blob/main/packages/pds/lexicons/app/bsky/embed/external.json and https://github.com/bluesky-social/atproto/blob/main/lexicons/com/atproto/repo/uploadBlob.json).</item>
/// <item>Retry: <c>com.atproto.repo.getRecord</c> on the attempt's rkey before sending again.</item>
/// </list>
/// </summary>
internal sealed class BlueskyChannel : ChannelBase
{
    private const int MaxGraphemes = 300;
    private const int MaxBytes = 3000;
    private const int MaxThumb = 1_000_000;
    private const int MaxTagGraphemes = 64;
    private const string Collection = "app.bsky.feed.post";

    /// <summary>app.bsky.embed.images: at most 4, each blob at most 2,000,000 bytes. An animated GIF goes, with a note that it shows still.</summary>
    private static readonly MediaRules Pictures = new(4, 2_000_000, int.MaxValue, AnimatedGifRule.Allowed, 2_000_000);

    private readonly BlueskyAccount _account;
    private readonly string _sessionKey;

    public BlueskyChannel(SocialChannelContext context, BlueskyAccount account) : base(context, [account.AppPassword])
    {
        _account = account;
        _sessionKey = $"{account.Service.AbsoluteUri}\n{account.Identifier}\n{Hash(account.AppPassword, 16)}";
        if (Fetch.PublicAddress.Refusal(account.Service, httpsOnly: true) is { } refusal)
            throw new ArgumentException($"The Bluesky service address is refused: {refusal}", nameof(account));
    }

    public override string Network => SocialNetworks.Bluesky;

    /// <summary>The handle or DID; an email identifier is not written into ids and logs, only a hash of it.</summary>
    public override string Id => _account.Identifier.Contains('@', StringComparison.Ordinal)
        ? "bluesky:email-" + Hash(_account.Identifier.ToUpperInvariant(), 12)
        : "bluesky:" + _account.Identifier;

    public override SocialIdempotency Idempotency { get; } = new(SocialIdempotencyKind.ReRead, null,
        "The record key is chosen before sending, so a retry reads it back first and never posts twice.");

    public override SocialAttempt BeginAttempt(SocialPost post) => new(Tid.Next(Now), Now);

    public override SocialComposition Compose(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        var tags = TextRules.NormalizeTags(post.Tags);
        var link = UsableLink(post);
        var text = TextRules.Assemble(post.Text, link, link is null ? null : TextRules.ShortLink(link), tags);
        var problems = new List<string>(LinkProblems(post));
        problems.AddRange(CardProblems(post));
        problems.AddRange(ImageProblems(post, Pictures));
        var length = TextRules.Graphemes(text);
        if (length > MaxGraphemes) problems.Add($"The post is {length} characters and Bluesky takes {MaxGraphemes}.");
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxBytes) problems.Add($"The post is {bytes} bytes and Bluesky takes {MaxBytes}.");
        foreach (var t in tags.Where(t => TextRules.Graphemes(t) > MaxTagGraphemes))
            problems.Add($"The tag #{t[..Math.Min(t.Length, 20)]}… is longer than Bluesky's {MaxTagGraphemes} characters.");
        if (text.Length == 0 && post.Card is null && post.Images.Count == 0) problems.Add("The post is empty.");
        return new SocialComposition(text, length, MaxGraphemes, "graphemes", problems);
    }

    protected override async Task<SocialPostResult> SendComposedAsync(SocialPost post, SocialComposition composed, SocialAttempt attempt, CancellationToken ct)
    {
        var notes = new List<string>();
        if (attempt.Resuming)
        {
            var (found, failure) = await ReadBackAsync(attempt.Token, ct).ConfigureAwait(false);
            if (failure is not null) return SocialPostResult.Failed(failure);
            if (found is not null) return found;
        }

        // Pictures first, each its own blob; one that doesn't upload stops the post, so none goes out short of a picture.
        var images = new JsonArray();
        for (var i = 0; i < post.Images.Count; i++)
        {
            var picture = post.Images[i];
            var (uploaded, uploadFailure) = await XrpcAsync(s =>
            {
                var r = new HttpRequestMessage(HttpMethod.Post, Xrpc("com.atproto.repo.uploadBlob")) { Content = new ReadOnlyMemoryContent(picture.Bytes) };
                r.Content.Headers.ContentType = new MediaTypeHeaderValue(NormalType(picture));
                return r;
            }, ct).ConfigureAwait(false);
            using (uploaded)
            {
                if (uploaded is not null && Prop(uploaded.RootElement, "blob") is { ValueKind: JsonValueKind.Object } blob)
                {
                    images.Add(new JsonObject { ["alt"] = picture.Alt, ["image"] = JsonNode.Parse(blob.GetRawText()) });
                    if (NormalType(picture) == "image/gif" && Media.IsAnimatedGif(picture.Bytes.Span))
                        notes.Add(Invariant($"Picture {i + 1} is an animated GIF; Bluesky shows a picture in a post as a still, so it doesn't move."));
                    continue;
                }
            }
            var why = uploadFailure ?? new SocialFailure(SocialFailureKind.Transient, "Bluesky's answer had no blob.");
            return SocialPostResult.Failed(why with
            {
                Message = Invariant($"Picture {i + 1} of {post.Images.Count} didn't upload ({why.Message}). Nothing was posted; Bluesky deletes the pictures already uploaded once no post uses them."),
                MayHavePosted = false,
            });
        }

        JsonNode? thumb = null;
        if (post.Images.Count == 0 && post.Link is not null && post.Card is not null
            && await LoadCardImageAsync(post.Card, MaxThumb, notes, ct).ConfigureAwait(false) is { } image)
        {
            var (blob, blobFailure) = await XrpcAsync(s =>
            {
                var r = new HttpRequestMessage(HttpMethod.Post, Xrpc("com.atproto.repo.uploadBlob")) { Content = new ByteArrayContent(image.Bytes) };
                r.Content.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
                return r;
            }, ct).ConfigureAwait(false);
            if (blob is not null && Prop(blob.RootElement, "blob") is { ValueKind: JsonValueKind.Object } b) thumb = JsonNode.Parse(b.GetRawText());
            else notes.Add($"The card picture didn't upload ({blobFailure?.Message ?? "no blob in the answer"}), so the card has no picture.");
            blob?.Dispose();
        }

        var (created, createFailure) = await XrpcAsync(s => new HttpRequestMessage(HttpMethod.Post, Xrpc("com.atproto.repo.createRecord"))
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["repo"] = s.Did,
                ["collection"] = Collection,
                ["rkey"] = attempt.Token,
                ["record"] = Record(post, composed.Text, thumb, images),
            }),
        }, ct).ConfigureAwait(false);
        using (created)
        {
            if (created is not null && Str(created.RootElement, "uri") is { } uri)
                return SocialPostResult.Success(uri, PostUrl(uri), notes);
        }

        // A resumed attempt that the read-back missed may have landed meanwhile (its record key is taken): look again.
        // The reference PDS answers a taken record key with a 500, not a 4xx, so any failure but the credentials looks again.
        if (attempt.Resuming && createFailure is { Kind: not SocialFailureKind.Auth and not SocialFailureKind.RateLimited })
        {
            var (found, _) = await ReadBackAsync(attempt.Token, ct).ConfigureAwait(false);
            if (found is not null) return found;
        }
        return SocialPostResult.Failed(createFailure ?? new SocialFailure(SocialFailureKind.Transient, "Bluesky's answer had no record URI.", MayHavePosted: true));
    }

    private async Task<(SocialPostResult? Found, SocialFailure? Failure)> ReadBackAsync(string rkey, CancellationToken ct)
    {
        string? did = null;
        var (doc, failure) = await XrpcAsync(s =>
        {
            did = s.Did;
            return new HttpRequestMessage(HttpMethod.Get, Xrpc("com.atproto.repo.getRecord")
                + $"?repo={Uri.EscapeDataString(s.Did)}&collection={Collection}&rkey={Uri.EscapeDataString(rkey)}");
        }, ct, notFoundIsAnswer: true).ConfigureAwait(false);
        using (doc)
        {
            if (failure is not null) return (null, failure);
            if (doc is not null && Str(doc.RootElement, "uri") is { } uri)
                return (SocialPostResult.Success(uri, PostUrl(uri), alreadyPosted: true), null);
            return (null, null);
        }
    }

    private JsonObject Record(SocialPost post, string text, JsonNode? thumb, JsonArray images)
    {
        var record = new JsonObject
        {
            ["$type"] = Collection,
            ["text"] = text,
            ["createdAt"] = Now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(post.Language)) record["langs"] = new JsonArray(post.Language);
        var facets = Facets(post, text);
        if (facets.Count > 0) record["facets"] = facets;
        if (images.Count > 0)
        {
            record["embed"] = new JsonObject { ["$type"] = "app.bsky.embed.images", ["images"] = images };
        }
        else if (post.Link is not null && post.Card is not null)
        {
            var external = new JsonObject
            {
                ["uri"] = post.Link.AbsoluteUri,
                ["title"] = post.Card.Title,
                ["description"] = post.Card.Description,
            };
            if (thumb is not null) external["thumb"] = thumb;
            record["embed"] = new JsonObject { ["$type"] = "app.bsky.embed.external", ["external"] = external };
        }
        return record;
    }

    /// <summary>A link facet over the shortened link and every full URL in the words, and a tag facet over every hashtag; byte offsets in UTF-8.</summary>
    private static JsonArray Facets(SocialPost post, string text)
    {
        var facets = new JsonArray();
        void Add(int start, int length, JsonObject feature)
        {
            var byteStart = Encoding.UTF8.GetByteCount(text.AsSpan(0, start));
            facets.Add(new JsonObject
            {
                ["index"] = new JsonObject { ["byteStart"] = byteStart, ["byteEnd"] = byteStart + Encoding.UTF8.GetByteCount(text.AsSpan(start, length)) },
                ["features"] = new JsonArray(feature),
            });
        }

        var body = (post.Text ?? "").Trim();
        if (post.Link is { } link && !body.Contains(link.AbsoluteUri, StringComparison.Ordinal))
        {
            var start = body.Length > 0 ? body.Length + 2 : 0;
            Add(start, TextRules.ShortLink(link).Length, new JsonObject { ["$type"] = "app.bsky.richtext.facet#link", ["uri"] = link.AbsoluteUri });
        }
        foreach (var (start, length) in TextRules.Urls(text))
            Add(start, length, new JsonObject { ["$type"] = "app.bsky.richtext.facet#link", ["uri"] = text.Substring(start, length) });
        foreach (var (start, length, tag) in TextRules.Hashtags(text))
            Add(start, length, new JsonObject { ["$type"] = "app.bsky.richtext.facet#tag", ["tag"] = tag });
        return facets;
    }

    private Uri? PostUrl(string atUri)
    {
        // at://<did>/app.bsky.feed.post/<rkey>; the DID keeps the address working when the handle changes.
        var parts = atUri.Split('/');
        return parts.Length == 5 && parts[0] == "at:" ? new Uri(_account.WebApp, $"/profile/{parts[2]}/post/{parts[4]}") : null;
    }

    private string Xrpc(string method) => new Uri(_account.Service, "/xrpc/" + method).AbsoluteUri;

    /// <summary>
    /// One XRPC call with the account's session: signs in when there is none, and once more when the access token has
    /// expired. Returns the answer's JSON, or a failure mapped from XRPC's <c>{ error, message }</c>.
    /// </summary>
    private async Task<(JsonDocument? Doc, SocialFailure? Failure)> XrpcAsync(Func<BlueskySession, HttpRequestMessage> make, CancellationToken ct,
        bool notFoundIsAnswer = false)
    {
        for (var round = 0; ; round++)
        {
            var (session, signInFailure) = await SessionAsync(ct).ConfigureAwait(false);
            if (session is null) return (null, signInFailure);
            using var request = make(session);
            request.Headers.Authorization = Bearer(session.AccessJwt);
            using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
            var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (IsSuccess(response)) return (doc, null);
            var error = doc is null ? null : Str(doc.RootElement, "error");
            var message = doc is null ? null : Str(doc.RootElement, "message");
            doc?.Dispose();
            if (notFoundIsAnswer && error == "RecordNotFound") return (null, null);
            if (error is "ExpiredToken" or "InvalidToken" && round == 0)
            {
                Context.Sessions.Drop(_sessionKey, session);
                continue;
            }
            return (null, Failure(response, error, message));
        }
    }

    private async Task<(BlueskySession? Session, SocialFailure? Failure)> SessionAsync(CancellationToken ct)
    {
        if (Context.Sessions.Get(_sessionKey) is { } cached)
        {
            AddSecret(cached.AccessJwt);
            AddSecret(cached.RefreshJwt);
            return (cached, null);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, Xrpc("com.atproto.server.createSession"))
        {
            Content = JsonContent.Create(new JsonObject { ["identifier"] = _account.Identifier, ["password"] = _account.AppPassword }),
        };
        using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!IsSuccess(response))
        {
            var error = doc is null ? null : Str(doc.RootElement, "error");
            var failure = Failure(response, error, doc is null ? null : Str(doc.RootElement, "message"));
            // Signing in creates nothing, so no failure here leaves a post behind.
            return (null, failure with
            {
                Kind = failure.Kind == SocialFailureKind.Rejected && error is "AuthenticationRequired" or "AccountTakedown" or "AuthFactorTokenRequired"
                    ? SocialFailureKind.Auth
                    : failure.Kind,
                MayHavePosted = false,
            });
        }
        if (doc is null || Str(doc.RootElement, "accessJwt") is not { } access || Str(doc.RootElement, "did") is not { } did)
            return (null, new SocialFailure(SocialFailureKind.Transient, "Bluesky's sign-in answer had no session."));
        AddSecret(access);
        AddSecret(Str(doc.RootElement, "refreshJwt"));
        var session = new BlueskySession(access, Str(doc.RootElement, "refreshJwt"), did, Str(doc.RootElement, "handle") ?? did);
        Context.Sessions.Set(_sessionKey, session);
        return (session, null);
    }

    private SocialFailure Failure(HttpResponseMessage response, string? error, string? message)
    {
        var said = error is null ? message : message is null ? error : $"{error}: {message}";
        var failure = FromStatus(response, said);
        return error is "AuthenticationRequired" or "AccountTakedown" or "AuthFactorTokenRequired" or "InvalidToken" or "ExpiredToken"
            && response.StatusCode is HttpStatusCode.BadRequest
            ? failure with { Kind = SocialFailureKind.Auth }
            : failure;
    }
}
