using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentKit.Channels;

/// <summary>
/// ntfy (ntfy.sh or a self-hosted server): a JSON publish to the server's root with the topic, title, message and click
/// link, and the access token as a bearer when the topic is protected. A topic on the public ntfy.sh is readable by anyone
/// who knows its name, so it is a secret: a long random one, or a protected topic with a token.
/// </summary>
public static partial class Ntfy
{
    /// <summary>What is kept of a message body (ntfy takes 4096 bytes before it turns a message into an attachment).</summary>
    public const int MaxMessage = 4000;

    /// <summary>The public server.</summary>
    public static readonly Uri PublicServer = new("https://ntfy.sh/");

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex TopicShape();

    /// <summary>Why <paramref name="server"/> can't be an ntfy server, or null: an absolute http(s) URL with no path, query or credentials.</summary>
    public static string? ServerRefusal(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.IsAbsoluteUri && server.Scheme is "http" or "https" && server.UserInfo.Length == 0
            && server.AbsolutePath is "/" && server.Query.Length == 0
            ? null
            : "An ntfy server is its address alone, such as https://ntfy.sh.";
    }

    /// <summary>Why <paramref name="topic"/> can't be a topic, or null: 1 to 64 letters, digits, <c>-</c> or <c>_</c>.</summary>
    public static string? TopicRefusal(string topic) =>
        TopicShape().IsMatch(topic ?? "") ? null : "An ntfy topic is 1 to 64 letters, digits, - or _.";

    /// <summary>The publish for <paramref name="message"/>.</summary>
    public static HttpRequestMessage Request(Uri server, string topic, string? token, ChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(message);
        if (TopicRefusal(topic) is { } why) throw new ArgumentException(why, nameof(topic));
        var payload = new JsonObject
        {
            ["topic"] = topic,
            ["title"] = Text.Truncate(message.Title.ReplaceLineEndings(" "), 250),
            ["message"] = Text.Truncate(message.Body.Length > 0 ? message.Body : message.Title, MaxMessage),
        };
        if (message.Link is { } link) payload["click"] = link.AbsoluteUri;
        var request = new HttpRequestMessage(HttpMethod.Post, server) { Content = JsonContent.Create(payload) };
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
