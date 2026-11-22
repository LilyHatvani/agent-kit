using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentKit.Mail.Tests;

/// <summary>A request as Resend received it, read before the client disposes it.</summary>
public sealed record SeenRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, byte[] Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>Resend, faked behind the package's own HttpClient: every request recorded, answered by <see cref="Respond"/>.</summary>
public sealed class FakeResend : HttpMessageHandler
{
    private readonly ConcurrentQueue<SeenRequest> _seen = new();

    public Func<SeenRequest, HttpResponseMessage> Respond { get; set; } = _ => Json(HttpStatusCode.OK, """{"id":"49a3999c-0ce1-4ea6-ab68-afcd6dc2e794"}""");

    public Func<SeenRequest, Exception?>? Fail { get; set; }

    public bool Hang { get; set; }

    public IReadOnlyList<SeenRequest> Seen => [.. _seen];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        byte[] body = [];
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        var seen = new SeenRequest(request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType?.MediaType, body);
        _seen.Enqueue(seen);
        if (Fail?.Invoke(seen) is { } e) throw e;
        if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
        return Respond(seen);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }
}

/// <summary>A clock the test moves; its timers fire only when <see cref="Advance"/> passes them.</summary>
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

    public int PendingTimers
    {
        get { lock (_timers) return _timers.Count(t => t.Due is not null); }
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

/// <summary>Every log line, formatted, with its category.</summary>
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

/// <summary>A host wired as a product wires the package (<c>AddResendEmail</c>), with Resend faked.</summary>
public sealed class TestHost : IDisposable
{
    public const string ApiKey = "re_TestKey_9f8e7d6c5b4a";

    public static readonly DateTimeOffset Start = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _services;

    public TestHost(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<TimeProvider>(Clock);
        configure?.Invoke(services);
        services.AddResendEmail(ApiKey);
        // Replace the guarded primary handler with the fake, as a product's tests do.
        services.AddHttpClient(MailDefaults.ResendHttpClientName).ConfigurePrimaryHttpMessageHandler(() => Resend);
        _services = services.BuildServiceProvider();
    }

    public FakeResend Resend { get; } = new();

    public TestClock Clock { get; } = new(Start);

    public CapturedLogs Logs { get; } = new();

    public IEmailSender Sender => _services.GetRequiredService<IEmailSender>();

    public void Dispose() => _services.Dispose();
}
