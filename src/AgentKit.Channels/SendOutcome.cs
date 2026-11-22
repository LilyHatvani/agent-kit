using System.Globalization;
using System.Net;
using System.Text;

namespace AgentKit.Channels;

/// <summary>What a destination's answer means for the delivery.</summary>
public enum SendResult
{
    /// <summary>Taken (2xx).</summary>
    Sent,
    /// <summary>Not now: rate limited, a timeout, a server error, the network. Try again after <see cref="SendOutcome.RetryAfter"/> or the schedule.</summary>
    Retry,
    /// <summary>The destination no longer exists or no longer takes this sender (401, 403, 404, 410): stop sending to it and say so.</summary>
    Gone,
    /// <summary>Refused for good (another 4xx): this message won't be taken however often it is sent.</summary>
    Failed,
}

/// <summary>A destination's answer, read: the result, the HTTP status, how long to wait, and a short error for the owner.</summary>
public sealed record SendOutcome(SendResult Result, int? Status, TimeSpan? RetryAfter, string? Error)
{
    /// <summary>The longest wait a <c>Retry-After</c> may ask for; a longer one is capped.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(12);

    private const int ErrorBytes = 300;

    /// <summary>A request that never got an answer (DNS, refused connection, timeout): retry.</summary>
    public static SendOutcome NoAnswer(string error) => new(SendResult.Retry, null, null, Clip(error));

    /// <summary>
    /// Reads <paramref name="response"/>: 2xx sent; 429 retry after its <c>Retry-After</c> (seconds or a date; capped at
    /// <see cref="MaxRetryAfter"/>); 408, 425 and 5xx retry; 401, 403, 404 and 410 gone; other 4xx failed. The error is
    /// the status and the start of the body (at most 300 bytes, decoded leniently).
    /// </summary>
    public static async Task<SendOutcome> ReadAsync(HttpResponseMessage response, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        var status = (int)response.StatusCode;
        var result = Classify(status);
        if (result == SendResult.Sent) return new(SendResult.Sent, status, null, null);
        var error = $"{status} {response.ReasonPhrase}".Trim();
        var body = await HeadAsync(response, ct).ConfigureAwait(false);
        if (body.Length > 0) error += ": " + body;
        return result switch
        {
            SendResult.Retry when status == 429 => new(SendResult.Retry, status, RetryAfterOf(response, now) ?? TimeSpan.FromMinutes(1), Clip(error)),
            SendResult.Retry => new(SendResult.Retry, status, RetryAfterOf(response, now), Clip(error)),
            _ => new(result, status, null, Clip(error)),
        };
    }

    /// <summary>
    /// What an HTTP status means for a delivery, without reading the body: 2xx sent; 429, 408, 425 and 5xx retry; 401,
    /// 403, 404 and 410 gone; anything else failed. <see cref="ReadAsync"/> reads every answer with it, and AgentKit.Social
    /// reads a network's refusal with it before saying what it means for a post.
    /// </summary>
    public static SendResult Classify(int status) => status switch
    {
        >= 200 and < 300 => SendResult.Sent,
        429 or 408 or 425 or >= 500 => SendResult.Retry,
        401 or 403 or 404 or 410 => SendResult.Gone,
        _ => SendResult.Failed,
    };

    /// <summary>
    /// How long <paramref name="response"/>'s <c>Retry-After</c> asks to wait: seconds (whole, or with a fraction as some
    /// servers write them) or an HTTP date, measured from <paramref name="now"/>. A wait in the past is none; one over
    /// <paramref name="max"/> (default <see cref="MaxRetryAfter"/>) is <paramref name="max"/>; a value that isn't a finite
    /// number or a date (<c>NaN</c>, <c>1e400</c>, words) is no header. Null when there is none.
    /// </summary>
    public static TimeSpan? RetryAfterOf(HttpResponseMessage response, DateTimeOffset now, TimeSpan? max = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var cap = max ?? MaxRetryAfter;
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, TimeSpan.Zero, nameof(max));
        var h = response.Headers.RetryAfter;
        if (h?.Delta is { } delta) return Clamp(delta, cap);
        if (h?.Date is { } date) return Clamp(date - now, cap);
        if (!response.Headers.TryGetValues("Retry-After", out var raw)
            || !double.TryParse(raw.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds))
            return null;
        // Compared as seconds before a TimeSpan is made: TimeSpan.FromSeconds throws on a value past its range.
        return seconds <= 0 ? TimeSpan.Zero : seconds >= cap.TotalSeconds ? cap : TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan Clamp(TimeSpan wait, TimeSpan cap) => wait < TimeSpan.Zero ? TimeSpan.Zero : wait > cap ? cap : wait;

    private static async Task<string> HeadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[ErrorBytes];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false)) > 0) read += n;
            return Encoding.UTF8.GetString(buffer, 0, read).ReplaceLineEndings(" ").Trim();
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException)
        {
            return "";
        }
    }

    private static string Clip(string s) => Text.Truncate(s, 500);
}

/// <summary>When to try a failed delivery again.</summary>
public static class Retries
{
    /// <summary>1 minute, 5 minutes, 30 minutes, 2 hours, 12 hours; then the delivery is dead.</summary>
    public static readonly IReadOnlyList<TimeSpan> Schedule =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(12)];

    /// <summary>
    /// The wait before attempt <paramref name="attemptsMade"/> + 1, or null when the schedule is spent. A destination's
    /// own <c>Retry-After</c> wins when it asks for longer.
    /// </summary>
    public static TimeSpan? Next(int attemptsMade, TimeSpan? retryAfter = null)
    {
        if (attemptsMade < 1 || attemptsMade > Schedule.Count) return null;
        var wait = Schedule[attemptsMade - 1];
        return retryAfter is { } r && r > wait ? r : wait;
    }
}
