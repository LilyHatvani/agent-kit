using System.Security.Cryptography;

namespace AgentKit.Social.Internal;

/// <summary>
/// AT Protocol timestamp identifiers (https://atproto.com/specs/tid), the record key type of <c>app.bsky.feed.post</c>:
/// 64 bits, the top one zero, then 53 bits of microseconds since the Unix epoch and a 10-bit clock id, written as 13
/// characters of base32-sortable. Strictly increasing within this process, so two attempts never share a key.
/// </summary>
internal static class Tid
{
    private const string Alphabet = "234567abcdefghijklmnopqrstuvwxyz";
    private static readonly int ClockId = RandomNumberGenerator.GetInt32(1024);
    private static long _last;

    public static string Next(DateTimeOffset now)
    {
        var micros = (now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
        long taken;
        long seen;
        do
        {
            seen = Interlocked.Read(ref _last);
            taken = Math.Max(micros, seen + 1);
        }
        while (Interlocked.CompareExchange(ref _last, taken, seen) != seen);

        var value = (((ulong)taken << 10) | (uint)ClockId) & 0x7FFF_FFFF_FFFF_FFFFUL;
        Span<char> chars = stackalloc char[13];
        for (var i = 12; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }
        return new string(chars);
    }

    /// <summary>Whether <paramref name="s"/> has a TID's syntax.</summary>
    public static bool IsValid(string s) =>
        s.Length == 13 && "234567abcdefghij".Contains(s[0], StringComparison.Ordinal) && s.All(c => Alphabet.Contains(c, StringComparison.Ordinal));
}
