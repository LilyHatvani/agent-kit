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

public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
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
}
