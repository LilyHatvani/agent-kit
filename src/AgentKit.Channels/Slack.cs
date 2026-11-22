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

    /// <summary>Why <paramref name="webhook"/> isn't a Slack incoming webhook URL, or null.</summary>
    public static string? WebhookRefusal(Uri webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        return webhook.IsAbsoluteUri && webhook.Scheme == Uri.UriSchemeHttps && webhook.IdnHost.Equals("hooks.slack.com", StringComparison.OrdinalIgnoreCase)
            && webhook.UserInfo.Length == 0 && webhook.IsDefaultPort
            && webhook.AbsolutePath.StartsWith("/services/", StringComparison.Ordinal) && webhook.AbsolutePath.Length > "/services/".Length
            ? null
            : "A Slack incoming webhook URL starts with https://hooks.slack.com/services/.";
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
