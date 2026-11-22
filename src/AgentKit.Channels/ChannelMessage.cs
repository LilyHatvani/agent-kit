using System.Text.Json.Nodes;

namespace AgentKit.Channels;

/// <summary>
/// One message, before it is shaped for a destination. <see cref="Title"/> and <see cref="Body"/> may hold text an agent
/// wrote (a post title): every destination escapes it for its own markup, so it can't mention everyone, mask a link or
/// break the layout.
/// </summary>
/// <param name="Type">What happened, for machines: an event kind such as <c>approval_requested</c>. Webhooks send it as <c>type</c>.</param>
/// <param name="Title">One line for people.</param>
/// <param name="Body">A few lines for people; may be empty.</param>
/// <param name="Link">Where tapping the message goes, or null.</param>
/// <param name="Data">Structured detail for webhooks (ids, states). Chat destinations ignore it.</param>
public sealed record ChannelMessage(string Type, string Title, string Body, Uri? Link, JsonObject? Data = null);

/// <summary>Shortening text to a destination's limit.</summary>
public static class Text
{
    /// <summary><paramref name="text"/> cut to at most <paramref name="max"/> characters, ending in "…" when cut, never inside a surrogate pair.</summary>
    public static string Truncate(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        if (text.Length <= max) return text;
        var cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + "…";
    }
}
