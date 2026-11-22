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
    [GeneratedRegex(@"(?<=^|\s)#([\p{L}\p{M}\p{N}_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    /// <summary>The URLs in <paramref name="text"/>, as (start, length) in UTF-16 units.</summary>
    public static IEnumerable<(int Start, int Length)> Urls(string text)
    {
        foreach (Match m in UrlPattern().Matches(text))
        {
            var length = m.Length;
            // A closing parenthesis stays when the URL opened one (…/Rust_(programming_language)), as Bluesky's own detector keeps it.
            while (length > 0 && ".,;:!?)]}'\"".Contains(text[m.Index + length - 1], StringComparison.Ordinal)
                   && !(text[m.Index + length - 1] == ')' && Balanced(text.AsSpan(m.Index, length))))
                length--;
            if (length > "https://".Length) yield return (m.Index, length);
        }
    }

    private static bool Balanced(ReadOnlySpan<char> url)
    {
        var depth = 0;
        foreach (var c in url)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
        }
        return depth == 0;
    }

    private static bool IsMark(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
        or UnicodeCategory.EnclosingMark;

    /// <summary>Lone UTF-16 surrogates (a text cut in the middle of an emoji) become U+FFFD; nothing moves, so indices hold.</summary>
    public static string Sanitize(string text)
    {
        char[]? fixedUp = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var lone = char.IsHighSurrogate(c) ? i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])
                : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1]));
            if (char.IsHighSurrogate(c) && !lone)
            {
                i++;
                continue;
            }
            if (!lone) continue;
            fixedUp ??= text.ToCharArray();
            fixedUp[i] = '�';
        }
        return fixedUp is null ? text : new string(fixedUp);
    }

    /// <summary>The hashtags in <paramref name="text"/>: (start of <c>#</c>, length with it, tag without it).</summary>
    public static IEnumerable<(int Start, int Length, string Tag)> Hashtags(string text)
    {
        foreach (Match m in TagPattern().Matches(text))
        {
            // Bluesky's detector gives an all-digit word (#2024) no tag; neither does this.
            if (m.Groups[1].Value.All(char.IsDigit)) continue;
            yield return (m.Index, m.Length, m.Groups[1].Value);
        }
    }

    /// <summary>The tags kept to letters, digits and <c>_</c>, without <c>#</c>, each once (ignoring case), in order.</summary>
    public static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var raw in tags ?? [])
        {
            var tag = new string([.. (raw ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || IsMark(c))]);
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
        var sb = new StringBuilder(Sanitize((text ?? "").Trim()));
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
    /// X's weighted length, never under X's own count (twitter-text config v3,
    /// https://github.com/twitter/twitter-text/blob/master/config/v3.json;
    /// https://docs.x.com/resources/fundamentals/counting-characters). Ported from Echo of Dreams' X announcement poster,
    /// whose rule is the same: a Compose that says "fits" must not meet X refusing the post.
    /// <list type="bullet">
    /// <item>The text in NFC. A code point in U+0000–U+10FF, U+2000–U+200D, U+2010–U+201F or U+2032–U+2037 weighs 1,
    /// any other 2.</item>
    /// <item>An emoji (a whole sequence, skin tone and keycap included) weighs 2, except a ZWJ join, which is charged per
    /// code point: X counts the sequences its emoji table knows as 2 and a newer one per part, and which table X runs
    /// can't be known.</item>
    /// <item>An http(s) URL weighs 23, as X wraps it. Another word that may be a link (a dot between letters or digits,
    /// a bare <c>example.com</c>) weighs the larger of its own weight and 23: X counts a domain it recognises as 23.</item>
    /// </list>
    /// </summary>
    public static int XWeightedLength(string text)
    {
        text = Sanitize(text).Normalize(NormalizationForm.FormC);
        var weight = 0;
        var at = 0;
        foreach (var (start, len) in Urls(text))
        {
            weight += XWeightWords(text[at..start]) + 23;
            at = start + len;
        }
        return weight + XWeightWords(text[at..]);
    }

    [GeneratedRegex(@"\S+|\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WordOrSpace();

    [GeneratedRegex(@"[\p{L}\p{N}]\.[\p{L}\p{N}]", RegexOptions.CultureInvariant)]
    private static partial Regex DomainLike();

    private static int XWeightWords(string text)
    {
        var total = 0;
        foreach (Match word in WordOrSpace().Matches(text))
        {
            var own = XWeight(word.Value);
            total += !char.IsWhiteSpace(word.Value[0]) && DomainLike().IsMatch(word.Value) ? Math.Max(own, 23) : own;
        }
        return total;
    }

    private static int XWeight(string text)
    {
        var total = 0;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
            total += GraphemeWeight((string)e.Current);
        return total;
    }

    private static int GraphemeWeight(string grapheme)
    {
        var weight = 0;
        var emoji = false;
        foreach (var rune in grapheme.EnumerateRunes())
        {
            var v = rune.Value;
            if (v >= 0x1F000 || v is >= 0x2300 and <= 0x23FF or >= 0x2600 and <= 0x27BF or >= 0x2B00 and <= 0x2BFF or 0x200D or 0xFE0F or 0x20E3)
                emoji = true;
            weight += v is <= 0x10FF or (>= 0x2000 and <= 0x200D) or (>= 0x2010 and <= 0x201F) or (>= 0x2032 and <= 0x2037) ? 1 : 2;
        }
        return emoji && !PerPart(grapheme) ? 2 : weight;
    }

    /// <summary>
    /// Sequences X may not count as one emoji, so they pay per part (never under): a ZWJ join (a sequence X's table
    /// doesn't know is counted part by part), a text-presentation selector (U+FE0E: <c>❤︎</c> counts 4 in
    /// twitter-text) and a keycap on anything but 0–9, # and * (<c>a⃣</c> counts 3).
    /// </summary>
    private static bool PerPart(string grapheme)
    {
        if (grapheme.Contains('‍', StringComparison.Ordinal) || grapheme.Contains('︎', StringComparison.Ordinal)) return true;
        return grapheme.Contains('⃣', StringComparison.Ordinal) && grapheme[0] is not ((>= '0' and <= '9') or '#' or '*');
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
