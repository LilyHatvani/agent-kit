namespace AgentKit.Social.Internal;

/// <summary>What a network does with an animated GIF.</summary>
internal enum AnimatedGifRule
{
    /// <summary>Posted like any picture.</summary>
    Allowed,

    /// <summary>Allowed only as the post's one picture (X: "up to 4 photos, 1 animated GIF or 1 video").</summary>
    Alone,
}

/// <summary>A network's limits on pictures in one post.</summary>
internal sealed record MediaRules(int MaxCount, long MaxBytes, int MaxAlt, AnimatedGifRule AnimatedGif, long MaxAnimatedGifBytes, long? MaxTotalBytes = null);

/// <summary>The bounds on waiting for a network to process an upload: resource bounds, not usage limits.</summary>
internal static class MediaPolling
{
    /// <summary>Refuses a negative interval (−1 ms would mean "forever") and a negative number of checks.</summary>
    public static void Validate(TimeSpan interval, int attempts, string paramName)
    {
        if (interval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(paramName, interval, "MediaPollInterval can't be negative.");
        if (attempts < 0)
            throw new ArgumentOutOfRangeException(paramName, attempts, "MediaPollAttempts can't be negative.");
    }
}

internal static class Media
{
    /// <summary>
    /// Whether a GIF has more than one frame, by walking its blocks (GIF89a, https://www.w3.org/Graphics/GIF/spec-gif89a.txt):
    /// the header and logical screen descriptor, the global colour table, then extensions (0x21) and images (0x2C) up to
    /// the trailer (0x3B). A file it can't walk counts as a still.
    /// </summary>
    public static bool IsAnimatedGif(ReadOnlySpan<byte> b)
    {
        if (b.Length < 13 || b[0] != (byte)'G' || b[1] != (byte)'I' || b[2] != (byte)'F') return false;
        var at = 13;
        if ((b[10] & 0x80) != 0) at += 3 * (1 << ((b[10] & 0x07) + 1));
        var frames = 0;
        while (at < b.Length)
        {
            switch (b[at])
            {
                case 0x3B:
                    return false;
                case 0x21:
                    at += 2;
                    if (!SkipSubBlocks(b, ref at)) return false;
                    break;
                case 0x2C:
                    if (++frames > 1) return true;
                    if (at + 10 > b.Length) return false;
                    var flags = b[at + 9];
                    at += 10;
                    if ((flags & 0x80) != 0) at += 3 * (1 << ((flags & 0x07) + 1));
                    at += 1; // LZW minimum code size
                    if (!SkipSubBlocks(b, ref at)) return false;
                    break;
                default:
                    return false;
            }
        }
        return false;
    }

    private static bool SkipSubBlocks(ReadOnlySpan<byte> b, ref int at)
    {
        while (at < b.Length)
        {
            var size = b[at];
            at += 1 + size;
            if (size == 0) return true;
        }
        return false;
    }
}
