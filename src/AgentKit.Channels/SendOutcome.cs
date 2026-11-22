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
        if (status is >= 200 and < 300) return new(SendResult.Sent, status, null, null);
        var error = $"{status} {response.ReasonPhrase}".Trim();
        var body = await HeadAsync(response, ct).ConfigureAwait(false);
        if (body.Length > 0) error += ": " + body;
        return status switch
        {
            429 => new(SendResult.Retry, status, RetryAfterOf(response, now) ?? TimeSpan.FromMinutes(1), Clip(error)),
            408 or 425 or >= 500 => new(SendResult.Retry, status, RetryAfterOf(response, now), Clip(error)),
            401 or 403 or 404 or 410 => new(SendResult.Gone, status, null, Clip(error)),
            _ => new(SendResult.Failed, status, null, Clip(error)),
        };
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response, DateTimeOffset now)
    {
        var h = response.Headers.RetryAfter;
        TimeSpan? wait = h?.Delta ?? (h?.Date is { } d ? d - now : null);
        if (wait is null && response.Headers.TryGetValues("Retry-After", out var raw)
            && double.TryParse(raw.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            wait = TimeSpan.FromSeconds(seconds);
        if (wait is null) return null;
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }

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
