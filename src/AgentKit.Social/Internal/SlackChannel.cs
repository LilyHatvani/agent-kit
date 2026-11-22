using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AgentKit.Social.Internal;

/// <summary>
/// Slack, by incoming webhook: one message, the link previewed by Slack itself from the page.
/// <list type="bullet">
/// <item><c>POST https://hooks.slack.com/services/…</c> with a JSON <c>text</c>; Slack answers <c>200 ok</c> as plain text
/// and errors as a short code in the body (<c>invalid_payload</c>, <c>channel_not_found</c>, <c>no_service</c>…), the codes
/// AgentKit.Channels' Slack notifications read too.</item>
/// <item>Escaping: <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> are written as entities, so a post's words can't open a
/// link, a mention or <c>&lt;!channel&gt;</c>. The link goes in angle brackets, Slack's own link form.</item>
/// <item>The answer carries no message id or address, and a webhook can't be asked what it posted.</item>
/// <item>A webhook can't attach pictures (only image blocks by public URL, which this package doesn't send): a post with
/// pictures is refused, never sent without them.</item>
/// </list>
/// </summary>
internal sealed class SlackChannel : ChannelBase
{
    /// <summary>
    /// Slack keeps a message's text readable to about 4,000 characters and truncates past 40,000 (from Slack's message
    /// guidance, recalled: its docs weren't reachable when this was written). 4,000 is the limit kept here.
    /// </summary>
    private const int MaxText = 4000;

    private readonly SlackWebhook _webhook;

    public SlackChannel(SocialChannelContext context, SlackWebhook webhook) : base(context, [webhook.Url.AbsolutePath, webhook.Url.AbsoluteUri])
    {
        _webhook = webhook;
        Id = "slack-webhook:" + Hash(webhook.Url.AbsoluteUri, 12);
    }

    public override string Network => SocialNetworks.Slack;

    public override string Id { get; }

    public override SocialIdempotency Idempotency { get; } = new(SocialIdempotencyKind.None, null,
        "A webhook can't be asked what it posted: a retry after a lost answer needs a person to check the channel.");

    public override SocialComposition Compose(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        var link = UsableLink(post);
        // Hashtags mean nothing on Slack, so they are left out. The count is of the text as people read it.
        var text = TextRules.Assemble(post.Text, link, link?.AbsoluteUri, [], includeTags: false);
        var problems = new List<string>(LinkProblems(post));
        if (post.Images.Count > 0)
            problems.Add("A Slack webhook can't carry pictures; Slack previews the link's page, picture included, on its own.");
        if (text.Length > MaxText) problems.Add($"The post is {text.Length} characters and Slack shows {MaxText}.");
        if (text.Length == 0) problems.Add("The post is empty.");
        return new SocialComposition(text, text.Length, MaxText, "characters", problems);
    }

    protected override async Task<SocialPostResult> SendComposedAsync(SocialPost post, SocialComposition composed, SocialAttempt attempt, CancellationToken ct)
    {
        if (attempt.Resuming && !attempt.ResendUnconfirmed)
            return SocialPostResult.Failed(SocialFailureKind.Unconfirmed,
                $"An earlier try began {attempt.StartedAt:u} and a webhook can't be asked whether it posted: check the channel before sending again.");

        var payload = new JsonObject
        {
            ["text"] = Format(composed.Text, UsableLink(post)),
            ["unfurl_links"] = true,
            ["unfurl_media"] = true,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _webhook.Url) { Content = JsonContent.Create(payload) };
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var body = (await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false)).Trim();
        if (!IsSuccess(response)) return SocialPostResult.Failed(Failure(response, body));
        if (!body.Equals("ok", StringComparison.OrdinalIgnoreCase))
            return SocialPostResult.Failed(SocialFailureKind.Transient, $"Slack answered {(int)response.StatusCode} without its \"ok\".", mayHavePosted: true);
        return SocialPostResult.Success("slack:" + attempt.Token, null, ["Slack's webhook doesn't say where the message is, so the post has no address."]);
    }

    /// <summary>The text with Slack's three escapes, and the link (where it stands as its own word) in Slack's link form.</summary>
    internal static string Format(string text, Uri? link)
    {
        var escaped = Escape(text);
        if (link is null) return escaped;
        var plain = Escape(link.AbsoluteUri);
        var at = escaped.LastIndexOf(plain, StringComparison.Ordinal);
        if (at < 0) return escaped;
        // A | in the address would end Slack's link early, so it is written percent-encoded inside the brackets.
        return escaped[..at] + "<" + plain.Replace("|", "%7C", StringComparison.Ordinal) + ">" + escaped[(at + plain.Length)..];
    }

    internal static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>
    /// Slack's codes: a webhook that is gone or a channel it can no longer post in (403, 404, 410: <c>no_service</c>,
    /// <c>channel_not_found</c>, <c>channel_is_archived</c>, <c>action_prohibited</c>…) is the credential; a 400 is the post.
    /// </summary>
    private SocialFailure Failure(HttpResponseMessage response, string body)
    {
        var failure = FromStatus(response, body.Length is > 0 and <= 200 ? body : null);
        return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone ? failure with { Kind = SocialFailureKind.Auth } : failure;
    }
}
