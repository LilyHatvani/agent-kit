using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentKit.Channels;
using AgentKit.Fetch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Mail;

/// <summary>The names and bounds this package uses.</summary>
public static class MailDefaults
{
    /// <summary>
    /// The HttpClient Resend is called through. Its primary handler connects only to public addresses on port 443
    /// (<c>AgentKit.Fetch</c>), follows no redirect and logs nothing. A test replaces the primary handler.
    /// </summary>
    public const string ResendHttpClientName = "AgentKit.Mail.Resend";

    /// <summary>Resend's API.</summary>
    public static readonly Uri ResendApi = new("https://api.resend.com/");

    /// <summary>How long one send may take before it is a timeout (and the email may have gone).</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The most an answer may be (64 KiB; Resend's is a few hundred bytes). A larger one is a lost answer, never buffered.</summary>
    public const int MaxAnswerBytes = 64 * 1024;
}

/// <summary>Registers the Resend sender.</summary>
public static class MailServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEmailSender"/> as a <see cref="ResendEmailSender"/> with <paramref name="apiKey"/>, unless the
    /// host registered its own first, and its HttpClient (<see cref="MailDefaults.ResendHttpClientName"/>): public
    /// addresses on port 443, no redirect, no request logging, an answer capped at <see cref="MailDefaults.MaxAnswerBytes"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The key is empty or not one HTTP header can carry.</exception>
    public static IServiceCollection AddResendEmail(this IServiceCollection services, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ResendEmailSender.CheckKey(apiKey);
        services.TryAddSingleton(TimeProvider.System);
        // No request logging: the default HttpClient logs write headers at Trace, the key with them. No client-wide
        // timeout: each send carries its own deadline on the host's clock.
        services.AddHttpClient(MailDefaults.ResendHttpClientName, c =>
            {
                c.Timeout = Timeout.InfiniteTimeSpan;
                c.MaxResponseContentBufferSize = MailDefaults.MaxAnswerBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => GuardedHttp.CreateHandler(new GuardedHttpOptions { Ports = [443] }))
            // No handler from the host's ConfigureHttpClientDefaults (a standard resilience handler retries a 503 three
            // times): one send is one request, and an email without a key would go out again. Applied after the
            // defaults whatever the registration order; a handler the host adds to this client by name still runs.
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear())
            .RemoveAllLoggers();
        services.TryAddSingleton<IEmailSender>(sp => new ResendEmailSender(sp.GetRequiredService<IHttpClientFactory>(), apiKey,
            sp.GetRequiredService<TimeProvider>(), sp.GetService<ILoggerFactory>()));
        return services;
    }
}

/// <summary>
/// Resend's REST API over a plain HttpClient: <c>POST https://api.resend.com/emails</c>
/// (https://resend.com/docs/api-reference/emails/send-email) with the key as a bearer, a <c>User-Agent</c> (Resend
/// refuses a request without one, https://resend.com/docs/api-reference/introduction), the email as JSON
/// (<c>from</c>, <c>to</c>, <c>subject</c>, <c>reply_to</c>, <c>html</c>, <c>text</c>, <c>headers</c>) and the
/// <c>Idempotency-Key</c> header (https://resend.com/docs/dashboard/emails/idempotency-keys). One email is always the
/// same bytes, so a resend under its key is the request Resend remembers.
/// </summary>
public sealed class ResendEmailSender : IEmailSender
{
    private const int MaxMessage = 300;
    private const int MaxRecipients = 50;
    private const int MaxKey = 256;

    private static readonly string UserAgent = "AgentKit.Mail/" +
        (typeof(ResendEmailSender).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0");

    private readonly IHttpClientFactory _http;
    private readonly string _apiKey;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    /// <summary>A sender with <paramref name="apiKey"/> (a Resend API key with sending access), over the client <see cref="MailDefaults.ResendHttpClientName"/>.</summary>
    /// <exception cref="ArgumentException">The key is empty or not one HTTP header can carry. The message never repeats it.</exception>
    public ResendEmailSender(IHttpClientFactory http, string apiKey, TimeProvider? clock = null, ILoggerFactory? logs = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        CheckKey(apiKey);
        _http = http;
        _apiKey = apiKey;
        _clock = clock ?? TimeProvider.System;
        _log = (logs ?? NullLoggerFactory.Instance).CreateLogger("AgentKit.Mail.Resend");
    }

    /// <inheritdoc />
    public override string ToString() => "Resend (key hidden)";

    internal static void CheckKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || !IsVisibleAscii(apiKey))
            throw new ArgumentException("A Resend API key is visible ASCII characters (re_…).", nameof(apiKey));
    }

    /// <summary>
    /// What stops <paramref name="email"/> being sent as it is, each a sentence; empty when nothing does. Only what Resend
    /// would refuse or what can't be put on the wire unchanged: no line breaks in an address or a header (a header
    /// injected through one), no half of a surrogate pair anywhere (JSON would write it as U+FFFD), 1 to 50 recipients,
    /// a body, a key of 1 to 256 printable ASCII characters that doesn't start or end with a space.
    /// </summary>
    public static IReadOnlyList<string> Problems(Email email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(email.From)) problems.Add("The email has no sender.");
        else if (HasControl(email.From)) problems.Add("The sender's address has a line break or a control character.");
        var to = email.To ?? [];
        if (to.Count is 0 or > MaxRecipients) problems.Add($"An email goes to 1 to {MaxRecipients} recipients; this one has {to.Count}.");
        if (to.Any(string.IsNullOrWhiteSpace)) problems.Add("A recipient is empty.");
        else if (to.Any(HasControl)) problems.Add("A recipient's address has a line break or a control character.");
        var replyTo = email.ReplyTo ?? [];
        if (replyTo.Any(string.IsNullOrWhiteSpace)) problems.Add("A reply-to address is empty.");
        else if (replyTo.Any(HasControl)) problems.Add("A reply-to address has a line break or a control character.");
        if (email.Subject is null) problems.Add("The email has no subject.");
        if (email.Text is null && email.Html is null) problems.Add("The email has no body: give it a text or an HTML one.");
        if (email.Headers is { } headers)
        {
            if (headers.Keys.Any(k => !IsHeaderName(k))) problems.Add("A header name isn't one (letters, digits and punctuation, no colon or space).");
            if (headers.Values.Any(v => v is null || HasControl(v))) problems.Add("A header value is missing or has a line break or a control character.");
            if (headers.Keys.Where(k => k is not null).GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                problems.Add("A header is given twice (names differing only in case are one header).");
        }
        string?[] texts = [email.From, .. to, .. replyTo, email.Subject, email.Text, email.Html,
            .. email.Headers?.Keys ?? [], .. email.Headers?.Values ?? []];
        if (texts.Any(t => t is not null && !IsWellFormed(t)))
            problems.Add("A field has half of a surrogate pair, which isn't text and can't be sent as written.");
        // Printable ASCII, a space inside allowed: HttpClient sends a header value as written except leading and trailing
        // whitespace, which it drops, and a character past ASCII has no one encoding in a header.
        if (email.IdempotencyKey is { } key
            && (key.Length is 0 or > MaxKey || !key.All(c => c is >= ' ' and < (char)0x7F) || key[0] == ' ' || key[^1] == ' '))
            problems.Add($"The idempotency key must be 1 to {MaxKey} printable ASCII characters, not starting or ending with a space.");
        return problems;
    }

    /// <summary>The request body for <paramref name="email"/>: the same email always gives the same bytes (headers in ordinal order).</summary>
    public static byte[] Body(Email email)
    {
        ArgumentNullException.ThrowIfNull(email);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("from", email.From);
            WriteList(w, "to", email.To);
            w.WriteString("subject", email.Subject);
            if (email.ReplyTo is { Count: > 0 }) WriteList(w, "reply_to", email.ReplyTo);
            if (email.Html is not null) w.WriteString("html", email.Html);
            if (email.Text is not null) w.WriteString("text", email.Text);
            if (email.Headers is { Count: > 0 } headers)
            {
                w.WriteStartObject("headers");
                foreach (var (name, value) in headers.OrderBy(h => h.Key, StringComparer.Ordinal)) w.WriteString(name, value);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <inheritdoc />
    public async Task<EmailResult> SendAsync(Email email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        cancellationToken.ThrowIfCancellationRequested();
        var problems = Problems(email);
        var result = problems.Count > 0
            ? EmailResult.Failed(new EmailFailure(EmailFailureKind.Rejected, Clean(string.Join(" ", problems))))
            : await PostAsync(email, cancellationToken).ConfigureAwait(false);
        // Kind and status only: Resend's message can name a recipient, and a recipient's address is personal data.
        if (result.Sent && _log.IsEnabled(LogLevel.Information))
            _log.LogInformation("Sent through Resend: {EmailId}", result.Id);
        else if (!result.Sent && _log.IsEnabled(LogLevel.Warning))
            _log.LogWarning("Resend didn't send an email ({Kind}, status {Status})", result.Failure!.Kind, result.Failure.Status);
        return result;
    }

    private async Task<EmailResult> PostAsync(Email email, CancellationToken ct)
    {
        try
        {
            var content = new ByteArrayContent(Body(email));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(MailDefaults.ResendApi, "emails")) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (email.IdempotencyKey is { } key) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            using var deadline = new CancellationTokenSource(MailDefaults.CallTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            using var response = await _http.CreateClient(MailDefaults.ResendHttpClientName).SendAsync(request, linked.Token).ConfigureAwait(false);
            // HttpClient buffered the answer already: reading it ignores a cancel that came after it arrived.
            var answer = await response.Content.ReadAsByteArrayAsync(CancellationToken.None).ConfigureAwait(false);
            return Read(response, answer, keyed: email.IdempotencyKey is not null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // Anything a transport or a handler the host added can throw is a result, never a throw. e.Message is never
            // used: it is the framework's, and nothing promises it holds no header.
            return EmailResult.Failed(FromException(e));
        }
    }

    private EmailResult Read(HttpResponseMessage response, byte[] answer, bool keyed)
    {
        var status = (int)response.StatusCode;
        var (id, name, message) = Parse(answer);
        if (SendOutcome.Classify(status) == SendResult.Sent)
        {
            return id is { Length: > 0 }
                ? EmailResult.Success(id)
                : EmailResult.Failed(new EmailFailure(EmailFailureKind.Transient,
                    $"Resend answered {status} without an email id, so whether it sent is unknown.", status, MayHaveSent: true));
        }
        var said = Clean(name is null ? message : message is null ? name : $"{name}: {message}");
        var detail = said.Length > 0 ? $": {said}" : ".";
        var failure = (status, name) switch
        {
            (401 or 403, _) => new EmailFailure(EmailFailureKind.Auth, Clean($"Resend refused the key or the account's setup ({status}){detail}"), status),
            (429, _) => new EmailFailure(EmailFailureKind.RateLimited, Clean($"Resend asked to wait ({status}){detail}"), status, RetryAfter(response)),
            (409, "invalid_idempotent_request") => new EmailFailure(EmailFailureKind.Rejected,
                Clean($"The idempotency key was used in the last 24 hours for a different email, which may have been sent ({status}){detail}"), status, MayHaveSent: true),
            // concurrent_idempotent_requests, or a 409 that doesn't say: another request with this key may be sending it.
            // Retrying under the same key can't send twice.
            (409, _) => new EmailFailure(EmailFailureKind.Transient, Clean(keyed
                ? $"Another request with this idempotency key is in progress ({status}){detail}"
                : $"Resend answered with a conflict ({status}){detail}"), status, MayHaveSent: true),
            ( >= 500, _) => new EmailFailure(EmailFailureKind.Transient, Clean($"Resend had a server error ({status}){detail}"), status, MayHaveSent: true),
            (408 or 425, _) => new EmailFailure(EmailFailureKind.Transient, Clean($"Resend didn't take the request in time ({status}){detail}"), status),
            _ => new EmailFailure(EmailFailureKind.Rejected, Clean($"Resend refused the email ({status}){detail}"), status),
        };
        return EmailResult.Failed(failure);
    }

    /// <summary>
    /// The wait a 429 asks for: <c>Retry-After</c> (Channels' <see cref="SendOutcome.RetryAfterOf"/>), else
    /// <c>ratelimit-reset</c>, which Resend documents as seconds until the window resets
    /// (https://resend.com/docs/api-reference/rate-limit). Both capped at <see cref="SendOutcome.MaxRetryAfter"/>.
    /// </summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (SendOutcome.RetryAfterOf(response, _clock.GetUtcNow()) is { } wait) return wait;
        return response.Headers.TryGetValues("ratelimit-reset", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            ? SendOutcome.Bounded(seconds, SendOutcome.MaxRetryAfter)
            : null;
    }

    private static EmailFailure FromException(Exception e)
    {
        if (GuardedHttp.Blocked(e) is not null)
            return new EmailFailure(EmailFailureKind.Transient, "Resend's address resolved to one that isn't on the public internet, so nothing was sent.");
        return e switch
        {
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
                new EmailFailure(EmailFailureKind.Transient, "Couldn't find Resend's server (DNS)."),
            HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError } =>
                new EmailFailure(EmailFailureKind.Transient, "Couldn't connect to Resend."),
            HttpRequestException => new EmailFailure(EmailFailureKind.Transient, "The connection to Resend broke before it answered.", MayHaveSent: true),
            TaskCanceledException or TimeoutException => new EmailFailure(EmailFailureKind.Transient, "Resend didn't answer in time.", MayHaveSent: true),
            _ => new EmailFailure(EmailFailureKind.Transient, "Resend's answer couldn't be read.", MayHaveSent: true),
        };
    }

    /// <summary>The answer's <c>id</c>, or its error <c>name</c> and <c>message</c> (Resend's SDKs read <c>{ statusCode, name, message }</c>).</summary>
    private static (string? Id, string? Name, string? Message) Parse(byte[] answer)
    {
        if (answer.Length == 0) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(answer);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object ? (Str(root, "id"), Str(root, "name"), Str(root, "message")) : (null, null, null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // Not JSON, or a string in it that isn't UTF-8. The whole answer (at most MaxAnswerBytes) goes to Clean, which
            // scrubs the key before it cuts: a cut first could leave the start of a key an echoing proxy wrote.
            return (null, null, Encoding.UTF8.GetString(answer));
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A message safe to show and log: one line, no control characters, no API key, at most 300 characters.</summary>
    private string Clean(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var sb = new StringBuilder(message.Length);
        foreach (var c in message) sb.Append(char.IsControl(c) ? ' ' : c);
        var s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        s = s.Replace(_apiKey, "***", StringComparison.Ordinal);
        return Text.Truncate(s, MaxMessage);
    }

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    private static bool HasControl(string s) => s.Any(char.IsControl);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Whether <paramref name="s"/> is whole UTF-16: every surrogate in a pair.</summary>
    private static bool IsWellFormed(string s)
    {
        try
        {
            StrictUtf8.GetByteCount(s);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsVisibleAscii(string s) => s.All(c => c is > ' ' and < (char)0x7F);

    /// <summary>An RFC 5322 field name: printable ASCII except the colon.</summary>
    private static bool IsHeaderName(string? s) => !string.IsNullOrEmpty(s) && IsVisibleAscii(s) && !s.Contains(':', StringComparison.Ordinal);
}
