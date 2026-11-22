using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentKit.Channels;

/// <summary>
/// A Discord channel webhook (<c>https://discord.com/api/webhooks/{id}/{token}</c>): one embed with the title, the body
/// and the link. Mentions are switched off (<c>allowed_mentions: { parse: [] }</c>), so a title an agent wrote can't ping
/// <c>@everyone</c>, and the body's markdown is escaped, so it can't mask a link.
/// </summary>
public static class Discord
{
    private static readonly string[] Hosts = ["discord.com", "discordapp.com", "canary.discord.com", "ptb.discord.com"];

    /// <summary>Discord's limit on an embed title.</summary>
    public const int MaxTitle = 256;
    /// <summary>Discord's limit on an embed description.</summary>
    public const int MaxBody = 4096;

    /// <summary>Why <paramref name="webhook"/> isn't a Discord webhook URL, or null.</summary>
    public static string? WebhookRefusal(Uri webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        if (!webhook.IsAbsoluteUri || webhook.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(webhook.IdnHost, StringComparer.OrdinalIgnoreCase)
            || webhook.UserInfo.Length > 0 || !webhook.IsDefaultPort)
            return "A Discord webhook URL starts with https://discord.com/api/webhooks/.";
        // /api/webhooks/{id}/{token}, or with an API version: /api/v10/webhooks/{id}/{token}.
        var path = webhook.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = path is ["api", "webhooks", ..] ? 2 : path is ["api", _, "webhooks", ..] ? 3 : -1;
        return at > 0 && path.Length == at + 2 && path[at].Length > 0 && path[at].All(char.IsAsciiDigit) && path[at + 1].Length > 0
            ? null
            : "That isn't a Discord webhook URL: copy it from the channel's Integrations → Webhooks.";
    }

    /// <summary>The POST for <paramref name="message"/>. <c>?wait=true</c> makes Discord answer only once the message is posted.</summary>
    public static HttpRequestMessage Request(Uri webhook, ChannelMessage message, string username = "Agent Kit")
    {
        ArgumentNullException.ThrowIfNull(webhook);
        ArgumentNullException.ThrowIfNull(message);
        var embed = new JsonObject
        {
            ["title"] = Text.Truncate(message.Title.ReplaceLineEndings(" "), MaxTitle),
        };
        if (message.Body.Length > 0) embed["description"] = Text.Truncate(Escape(message.Body), MaxBody);
        if (message.Link is { } link) embed["url"] = link.AbsoluteUri;
        var payload = new JsonObject
        {
            ["username"] = Text.Truncate(username, 80),
            ["embeds"] = new JsonArray(embed),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };
        var url = new UriBuilder(webhook) { Query = "wait=true" }.Uri;
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
    }

    /// <summary>Discord markdown escaped: <c>\ * _ ~ ` | &gt; # - [ ] ( ) &lt; @</c> each get a backslash, so text shows as typed.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '#' or '-' or '[' or ']' or '(' or ')' or '<' or '@') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
