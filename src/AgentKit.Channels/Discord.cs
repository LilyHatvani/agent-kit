using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentKit.Channels;

/// <summary>
/// A Discord channel webhook (<c>https://discord.com/api/webhooks/{id}/{token}</c>): one embed with the title, the body
/// and the link. Mentions are switched off (<c>allowed_mentions: { parse: [] }</c>), so a title an agent wrote can't ping
/// <c>@everyone</c>, and the body's markdown is escaped, so it can't mask a link. The webhook reading, the no-ping object
/// and the limits here are the ones AgentKit.Social's Discord channel posts with too.
/// </summary>
public static class Discord
{
    /// <summary>Discord's limit on an embed title.</summary>
    public const int MaxTitle = 256;
    /// <summary>Discord's limit on an embed description.</summary>
    public const int MaxBody = 4096;
    /// <summary>Discord's limit on a message's content.</summary>
    public const int MaxContent = 2000;
    /// <summary>Discord's limit on an embed footer's text.</summary>
    public const int MaxFooter = 2048;
    /// <summary>Discord's limit on an attachment's description (its alt text).</summary>
    public const int MaxAttachmentDescription = 1024;
    /// <summary>Discord's limit on the name a webhook message shows.</summary>
    public const int MaxUsername = 80;

    /// <summary>Why <paramref name="webhook"/> isn't a Discord webhook URL, or null. The rule is <see cref="DiscordWebhookAddress.TryParse"/>.</summary>
    public static string? WebhookRefusal(Uri webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        return DiscordWebhookAddress.TryParse(webhook, out _, out var refusal) ? null : refusal;
    }

    /// <summary>
    /// <c>{ "parse": [] }</c>, a fresh object for a payload's <c>allowed_mentions</c>: nothing in the message pings anyone,
    /// whatever its text says (<c>@everyone</c>, <c>&lt;@&amp;role&gt;</c>, a user).
    /// </summary>
    public static JsonObject NoMentions() => new() { ["parse"] = new JsonArray() };

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
            ["username"] = Text.Truncate(username, MaxUsername),
            ["embeds"] = new JsonArray(embed),
            ["allowed_mentions"] = NoMentions(),
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
