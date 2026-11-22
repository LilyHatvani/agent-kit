using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentKit.Social.Internal;

/// <summary>How each network counts a text, and how a post's words, link and tags are put together.</summary>
internal static partial class TextRules
{
    /// <summary>An http(s) URL in running text, up to whitespace; trailing sentence punctuation is left out.</summary>
    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    /// <summary>A hashtag at the start or after whitespace.</summary>
    [GeneratedRegex(@"(?<=^|\s)#([\p{L}\p{N}_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    /// <summary>The URLs in <paramref name="text"/>, as (start, length) in UTF-16 units.</summary>
    public static IEnumerable<(int Start, int Length)> Urls(string text)
    {
        foreach (Match m in UrlPattern().Matches(text))
        {
            var length = m.Length;
            while (length > 0 && ".,;:!?)]}'\"".Contains(text[m.Index + length - 1], StringComparison.Ordinal)) length--;
            if (length > "https://".Length) yield return (m.Index, length);
        }
    }

    /// <summary>The hashtags in <paramref name="text"/>: (start of <c>#</c>, length with it, tag without it).</summary>
    public static IEnumerable<(int Start, int Length, string Tag)> Hashtags(string text)
    {
        foreach (Match m in TagPattern().Matches(text))
            yield return (m.Index, m.Length, m.Groups[1].Value);
    }

    /// <summary>The tags kept to letters, digits and <c>_</c>, without <c>#</c>, each once (ignoring case), in order.</summary>
    public static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var raw in tags ?? [])
        {
            var tag = new string([.. (raw ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_')]);
            if (tag.Length > 0 && seen.Add(tag)) kept.Add(tag);
        }
        return kept;
    }

    /// <summary>
    /// The words, then the link on its own paragraph (unless the words hold it), then the tags not already in the words.
    /// <paramref name="linkText"/> is how the link reads in this network's text.
    /// </summary>
    public static string Assemble(string text, Uri? link, string? linkText, IReadOnlyList<string> tags, bool includeTags = true)
    {
        var sb = new StringBuilder((text ?? "").Trim());
        if (link is not null && linkText is not null && !sb.ToString().Contains(link.AbsoluteUri, StringComparison.Ordinal))
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(linkText);
        }
        if (includeTags)
        {
            var present = new HashSet<string>(Hashtags(sb.ToString()).Select(h => h.Tag), StringComparer.OrdinalIgnoreCase);
            var missing = tags.Where(t => !present.Contains(t)).Select(t => "#" + t).ToList();
            if (missing.Count > 0)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.AppendJoin(' ', missing);
            }
        }
        return sb.ToString();
    }

    /// <summary>User-perceived characters (extended grapheme clusters, UAX #29).</summary>
    public static int Graphemes(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>
    /// Mastodon: graphemes, with every URL counted as <paramref name="perUrl"/> characters
    /// (<see href="https://docs.joinmastodon.org/methods/instance/#v2">configuration.statuses</see>).
    /// </summary>
    public static int MastodonLength(string text, int perUrl)
    {
        var length = 0;
        var at = 0;
        foreach (var (start, len) in Urls(text))
        {
            length += Graphemes(text[at..start]) + perUrl;
            at = start + len;
        }
        return length + Graphemes(text[at..]);
    }

    /// <summary>
    /// X's weighted length (twitter-text config v3, https://github.com/twitter/twitter-text/blob/master/config/v3.json;
    /// https://docs.x.com/resources/fundamentals/counting-characters): the text in NFC; code points in U+0000–U+10FF,
    /// U+2000–U+200D, U+2010–U+201F and U+2032–U+2037 weigh 1, every other code point 2; an emoji weighs 2 whatever its
    /// sequence; every URL weighs 23.
    /// </summary>
    public static int XWeightedLength(string text)
    {
        text = text.Normalize(NormalizationForm.FormC);
        var weight = 0;
        var at = 0;
        foreach (var (start, len) in Urls(text))
        {
            weight += XWeight(text[at..start]) + 23;
            at = start + len;
        }
        return weight + XWeight(text[at..]);
    }

    private static int XWeight(string text)
    {
        var total = 0;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (IsEmoji(element))
            {
                total += 2;
                continue;
            }
            foreach (var rune in element.EnumerateRunes())
                total += rune.Value is <= 0x10FF or (>= 0x2000 and <= 0x200D) or (>= 0x2010 and <= 0x201F) or (>= 0x2032 and <= 0x2037) ? 1 : 2;
        }
        return total;
    }

    /// <summary>A grapheme that draws as an emoji: a pictograph, a regional-indicator flag, a keycap, or text with the emoji variation selector.</summary>
    private static bool IsEmoji(string element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= 0x1F000 and <= 0x1FAFF || v is >= 0x2600 and <= 0x27BF || v is 0xFE0F or 0x20E3 || v is >= 0x2B00 and <= 0x2BFF)
                return true;
        }
        return false;
    }

    /// <summary>Bluesky's short form of a link in text: no scheme, at most 30 characters, cut with an ellipsis (as the Bluesky app does).</summary>
    public static string ShortLink(Uri link)
    {
        var s = link.AbsoluteUri;
        s = s.StartsWith("https://", StringComparison.Ordinal) ? s[8..] : s.StartsWith("http://", StringComparison.Ordinal) ? s[7..] : s;
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        if (s.EndsWith('/') && s.IndexOf('/', StringComparison.Ordinal) == s.Length - 1) s = s[..^1];
        return s.Length > 30 ? s[..27] + "..." : s;
    }

    /// <summary>A field cut to <paramref name="max"/> UTF-16 units with an ellipsis, never splitting a grapheme.</summary>
    public static string Cut(string value, int max)
    {
        if (value.Length <= max) return value;
        var e = StringInfo.GetTextElementEnumerator(value);
        var end = 0;
        while (e.MoveNext() && e.ElementIndex + ((string)e.Current).Length <= max - 1) end = e.ElementIndex + ((string)e.Current).Length;
        return value[..end] + "…";
    }
}
