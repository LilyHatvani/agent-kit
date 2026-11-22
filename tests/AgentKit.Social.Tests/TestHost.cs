using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentKit.Social.Tests;

/// <summary>A request as the network received it: everything a test asserts on, read before the client disposes it.</summary>
public sealed record SeenRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);

    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    /// <summary>The form fields of an <c>application/x-www-form-urlencoded</c> body, in order.</summary>
    public List<KeyValuePair<string, string>> Form() =>
        [.. Text.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Select(kv =>
            new KeyValuePair<string, string>(WebUtility.UrlDecode(kv[0]), WebUtility.UrlDecode(kv.Length > 1 ? kv[1] : "")))];
}

/// <summary>
/// The networks, faked behind the package's own HttpClients: every request is recorded and answered by
/// <see cref="Respond"/>. Nothing leaves the process.
/// </summary>
public sealed class FakeNetwork : HttpMessageHandler
{
    private readonly ConcurrentQueue<SeenRequest> _seen = new();

    public Func<SeenRequest, HttpResponseMessage> Respond { get; set; } = r => throw new InvalidOperationException($"No answer for {r.Method} {r.Uri}.");

    /// <summary>When set, sending throws this instead of answering (a dropped connection, a timeout).</summary>
    public Func<SeenRequest, Exception?>? Fail { get; set; }

    /// <summary>When it says so, the request is never answered: it waits until its token is cancelled.</summary>
    public Func<SeenRequest, bool>? Hang { get; set; }

    public IReadOnlyList<SeenRequest> Seen => [.. _seen];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        byte[] body = [];
        string? contentType = null;
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            contentType = request.Content.Headers.ContentType?.MediaType;
        }
        var seen = new SeenRequest(request.Method, request.RequestUri!, headers, contentType, body);
        _seen.Enqueue(seen);
        if (Fail?.Invoke(seen) is { } e) throw e;
        if (Hang?.Invoke(seen) == true) await Task.Delay(Timeout.Infinite, cancellationToken);
        return Respond(seen);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, object body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    public static HttpResponseMessage Bytes(byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }
}

/// <summary>
/// A clock the test moves. Its timers fire only when <see cref="Advance"/> passes their time, so a deadline the package
/// sets on the host's clock (a call's, a cleanup's) runs out exactly when a test says.
/// </summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by)
    {
        Now += by;
        List<ManualTimer> due;
        lock (_timers) due = [.. _timers.Where(t => t.Due is { } d && d <= Now)];
        foreach (var timer in due) timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    private void Remove(ManualTimer timer)
    {
        lock (_timers) _timers.Remove(timer);
    }

    private sealed class ManualTimer(TestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = null;
            callback(state);
        }

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Every log line written through the host's logging, formatted, with its category.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>A host wired the way a product wires the package, with the networks faked.</summary>
public sealed class TestHost : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _services;

    public TestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSocialChannels();
        // Replace the primary handlers (the guarded ones) with the fakes, as a product's tests do.
        services.AddHttpClient(SocialDefaults.ApiHttpClientName).ConfigurePrimaryHttpMessageHandler(() => Network);
        services.AddHttpClient(SocialDefaults.MediaHttpClientName).ConfigurePrimaryHttpMessageHandler(() => Media);
        _services = services.BuildServiceProvider();
    }

    public FakeNetwork Network { get; } = new();

    public FakeNetwork Media { get; } = new();

    public TestClock Clock { get; } = new(Start);

    public CapturedLogs Logs { get; } = new();

    public SocialChannels Channels => _services.GetRequiredService<SocialChannels>();

    public SocialPublisher Publisher => _services.GetRequiredService<SocialPublisher>();

    public InMemorySocialPostLedger Ledger => (InMemorySocialPostLedger)_services.GetRequiredService<ISocialPostLedger>();

    public void Dispose() => _services.Dispose();
}

public static class Pictures
{
    /// <summary>The first bytes of a PNG, then padding: enough for the package to tell it's a PNG.</summary>
    public static byte[] Png(int size)
    {
        var b = new byte[size];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(b, 0);
        return b;
    }

    /// <summary>A real 1×1 GIF89a with <paramref name="frames"/> frames (two or more make it animated), padded to <paramref name="padTo"/> bytes.</summary>
    public static byte[] Gif(int frames, int padTo = 0)
    {
        var b = new List<byte>();
        b.AddRange("GIF89a"u8.ToArray());
        b.AddRange([1, 0, 1, 0, 0x80, 0, 0]);               // 1×1, a global colour table of 2 entries
        b.AddRange([0, 0, 0, 255, 255, 255]);
        b.AddRange([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8.ToArray(), 0x03, 0x01, 0x00, 0x00, 0x00]);
        for (var i = 0; i < frames; i++)
        {
            b.AddRange([0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00]);   // graphic control
            b.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0]);                   // image descriptor
            b.AddRange([0x02, 0x02, 0x44, 0x01, 0x00]);                      // LZW data
        }
        if (padTo > b.Count + 1)
        {
            // A comment extension of padding, in sub-blocks of 255.
            b.AddRange([0x21, 0xFE]);
            var left = padTo - b.Count - 2;
            while (left > 0)
            {
                var n = Math.Min(255, Math.Max(1, left - 1));
                b.Add((byte)n);
                b.AddRange(new byte[n]);
                left -= n + 1;
            }
            b.Add(0x00);
        }
        b.Add(0x3B);
        return [.. b];
    }

    public static byte[] AnimatedGif(int padTo = 0) => Gif(2, padTo);
}
