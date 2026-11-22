using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AgentKit.Channels;

/// <summary>
/// A Slack incoming webhook (<c>https://hooks.slack.com/services/…</c>): a fallback <c>text</c> and one section block,
/// the title as a link and the body below. <c>&amp; &lt; &gt;</c> are escaped (Slack's rule), so agent-written text can't
/// open a link or a mention (<c>&lt;!channel&gt;</c>).
/// </summary>
public static class Slack
{
    /// <summary>Slack's limit on a section block's text.</summary>
    public const int MaxSection = 3000;

    /// <summary>
    /// Why <paramref name="webhook"/> isn't a Slack incoming webhook URL, or null: https on <c>hooks.slack.com</c>, the
    /// default port, no user name, a path under <c>/services/</c>, no query or fragment (Slack hands out none). The one
    /// check of a Slack webhook in this repository: AgentKit.Social's <c>SlackWebhook</c> is built on it. The refusal names
    /// what is wrong (a host shows it to the person who pasted the URL) and never repeats the URL.
    /// </summary>
    public static string? WebhookRefusal(Uri webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        if (!(webhook.IsAbsoluteUri && webhook.Scheme == Uri.UriSchemeHttps && webhook.IdnHost.Equals("hooks.slack.com", StringComparison.OrdinalIgnoreCase)
              && webhook.UserInfo.Length == 0 && webhook.IsDefaultPort
              && webhook.AbsolutePath.StartsWith("/services/", StringComparison.Ordinal) && webhook.AbsolutePath.Length > "/services/".Length))
            return "A Slack incoming webhook URL starts with https://hooks.slack.com/services/.";
        return webhook.Query.Length == 0 && webhook.Fragment.Length == 0
            ? null
            : "A Slack incoming webhook URL has no query or fragment (nothing after ? or #): copy it again from the app's Incoming Webhooks page.";
    }

    /// <summary>The POST for <paramref name="message"/>.</summary>
    public static HttpRequestMessage Request(Uri webhook, ChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        ArgumentNullException.ThrowIfNull(message);
        var title = Escape(message.Title.ReplaceLineEndings(" "));
        var head = message.Link is { } link ? $"*<{link.AbsoluteUri}|{title}>*" : $"*{title}*";
        var section = message.Body.Length > 0 ? head + "\n" + Escape(message.Body) : head;
        var payload = new JsonObject
        {
            ["text"] = Text.Truncate(message.Title.ReplaceLineEndings(" "), 300),
            ["blocks"] = new JsonArray(new JsonObject
            {
                ["type"] = "section",
                ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = Text.Truncate(section, MaxSection) },
            }),
            ["unfurl_links"] = false,
        };
        return new HttpRequestMessage(HttpMethod.Post, webhook) { Content = JsonContent.Create(payload) };
    }

    /// <summary>Slack's three escapes: <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
