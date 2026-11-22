using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentKit.Fetch;
using Microsoft.Extensions.Logging;

namespace AgentKit.Social.Internal;

/// <summary>What every adapter shares: the HTTP clients, the clock, failure mapping, and keeping secrets out of text.</summary>
internal abstract class ChannelBase : ISocialChannel
{
    private const int MaxMessage = 300;
    private readonly Lock _secretsGate = new();
    private string[] _secrets;

    protected ChannelBase(SocialChannelContext context, IEnumerable<string?> secrets)
    {
        Context = context;
        Log = context.Logs.CreateLogger("AgentKit.Social." + GetType().Name.Replace("Channel", "", StringComparison.Ordinal));
        _secrets = Ordered(secrets);
    }

    /// <summary>Longest first, so a secret that contains another is scrubbed whole.</summary>
    private static string[] Ordered(IEnumerable<string?> secrets) =>
        [.. secrets.Where(s => !string.IsNullOrEmpty(s) && s.Length >= 4).Select(s => s!).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length)];

    /// <summary>Adds a credential learned while working (a session token) to what is scrubbed from messages.</summary>
    protected void AddSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || _secrets.Contains(secret, StringComparer.Ordinal)) return;
        lock (_secretsGate) _secrets = Ordered([.. _secrets, secret]);
    }

    protected SocialChannelContext Context { get; }

    protected ILogger Log { get; }

    protected HttpClient Api => Context.Http.CreateClient(SocialDefaults.ApiHttpClientName);

    protected DateTimeOffset Now => Context.Clock.GetUtcNow();

    public abstract string Network { get; }

    public abstract string Id { get; }

    public abstract SocialIdempotency Idempotency { get; }

    public abstract SocialComposition Compose(SocialPost post);

    public virtual SocialAttempt BeginAttempt(SocialPost post) =>
        new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), Now);

    public async Task<SocialPostResult> SendAsync(SocialPost post, SocialAttempt attempt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(post.IdempotencyKey, nameof(post));
        cancellationToken.ThrowIfCancellationRequested();
        var composed = Compose(post);
        SocialPostResult result;
        if (!composed.Fits)
        {
            result = SocialPostResult.Failed(SocialFailureKind.Rejected, string.Join(" ", composed.Problems));
        }
        else
        {
            try
            {
                result = await SendComposedAsync(post, composed, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or TimeoutException or JsonException)
            {
                result = SocialPostResult.Failed(FromException(e));
            }
        }
        if (result.Posted && Log.IsEnabled(LogLevel.Information))
            Log.LogInformation("Posted to {Channel}: {PostId}", Id, result.PostId);
        else if (!result.Posted && Log.IsEnabled(LogLevel.Warning))
            Log.LogWarning("Post to {Channel} failed ({Kind}): {Message}", Id, result.Failure!.Kind, result.Failure.Message);
        return result;
    }

    /// <summary>What every network refuses in a card: a card without the link it previews, a picture type that isn't an image type.</summary>
    protected static IEnumerable<string> CardProblems(SocialPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (post.Card is null) yield break;
        if (post.Link is null) yield return "A card needs the link it previews.";
        if (post.Card.ImageContentType is { } type
            && (!MediaTypeHeaderValue.TryParse(type, out var parsed) || parsed.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true))
            yield return "The card picture's media type must be an image type, such as image/png.";
    }

    /// <summary>Sends a post that fits. Expected failures come back as results; transport ones may throw for the caller to map.</summary>
    protected abstract Task<SocialPostResult> SendComposedAsync(SocialPost post, SocialComposition composed, SocialAttempt attempt, CancellationToken ct);

    /// <summary>
    /// A transport failure as a result. Blocked by the address guard or refused before a connection: nothing was sent.
    /// A timeout or an unreadable answer: the network may have acted.
    /// </summary>
    protected SocialFailure FromException(Exception e)
    {
        if (GuardedHttp.Blocked(e) is { } blocked)
            return new SocialFailure(SocialFailureKind.Rejected, Clean(blocked.Message));
        return e switch
        {
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
                new SocialFailure(SocialFailureKind.Transient, $"Couldn't find {Name}'s server (DNS)."),
            HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError } =>
                new SocialFailure(SocialFailureKind.Transient, $"Couldn't connect to {Name}."),
            HttpRequestException => new SocialFailure(SocialFailureKind.Transient, $"The connection to {Name} broke before it answered.", MayHavePosted: true),
            TaskCanceledException or TimeoutException => new SocialFailure(SocialFailureKind.Transient, $"{Name} didn't answer in time.", MayHavePosted: true),
            _ => new SocialFailure(SocialFailureKind.Transient, $"{Name}'s answer couldn't be read.", MayHavePosted: true),
        };
    }

    /// <summary>
    /// The usual mapping of a refused HTTP answer: 401 and 403 are the credentials, 429 a wait, other 4xx a rejection with
    /// the network's message, 5xx transient (and the post may be up).
    /// </summary>
    protected SocialFailure FromStatus(HttpResponseMessage response, string? networkMessage)
    {
        ArgumentNullException.ThrowIfNull(response);
        var status = (int)response.StatusCode;
        var said = Clean(networkMessage);
        var detail = said.Length > 0 ? $": {said}" : ".";
        return status switch
        {
            401 or 403 => new SocialFailure(SocialFailureKind.Auth, Clean($"{Name} refused the credentials ({status}){detail}")),
            429 => new SocialFailure(SocialFailureKind.RateLimited, Clean($"{Name} asked to wait{detail}"), RetryAfter(response)),
            >= 500 => new SocialFailure(SocialFailureKind.Transient, Clean($"{Name} had a server error ({status}){detail}"), MayHavePosted: true),
            _ => new SocialFailure(SocialFailureKind.Rejected, Clean($"{Name} refused the post ({status}){detail}")),
        };
    }

    /// <summary>The network's name as people write it.</summary>
    protected string Name => Network switch
    {
        SocialNetworks.Bluesky => "Bluesky",
        SocialNetworks.Mastodon => "Mastodon",
        SocialNetworks.Discord => "Discord",
        SocialNetworks.X => "X",
        _ => Network,
    };

    /// <summary>
    /// How long a 429 asks to wait: <c>Retry-After</c> (seconds or a date), then the network's reset headers
    /// (<c>ratelimit-reset</c> and <c>x-rate-limit-reset</c> in Unix seconds, <c>X-RateLimit-Reset</c> as Unix seconds or an ISO 8601 date,
    /// <c>X-RateLimit-Reset-After</c> in seconds).
    /// </summary>
    protected TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var now = Now;
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } d) return d;
            if (ra.Date is { } date) return Positive(date - now);
        }
        if (Header(response, "X-RateLimit-Reset-After") is { } after
            && double.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return Positive(TimeSpan.FromSeconds(seconds));
        foreach (var name in new[] { "ratelimit-reset", "x-rate-limit-reset", "X-RateLimit-Reset" })
        {
            if (Header(response, name) is not { } value) continue;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch) && epoch > 1_000_000_000)
                return Positive(DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000)) - now);
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                return Positive(at - now);
        }
        return null;
    }

    private static TimeSpan Positive(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>A message safe to show and log: one line, no control characters, no credential, at most 300 characters.</summary>
    protected string Clean(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var sb = new StringBuilder(message.Length);
        foreach (var c in message)
            sb.Append(char.IsControl(c) ? ' ' : c);
        var s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var secret in _secrets)
            s = s.Replace(secret, "***", StringComparison.Ordinal);
        return s.Length > MaxMessage ? s[..(MaxMessage - 1)] + "…" : s;
    }

    /// <summary>Reads a JSON body, or null when it isn't JSON.</summary>
    protected static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length == 0) return null;
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { return null; }
    }

    protected static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The card picture: the host's bytes, or <see cref="LinkCard.ImageUrl"/> fetched through the media client (public
    /// addresses only, at most <paramref name="maxBytes"/>). A picture that can't be had is a note, never a failed post.
    /// </summary>
    protected async Task<(byte[] Bytes, string ContentType)?> LoadCardImageAsync(LinkCard? card, int maxBytes, List<string> notes, CancellationToken ct)
    {
        if (card is null) return null;
        if (!card.Image.IsEmpty)
        {
            if (card.Image.Length > maxBytes)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"The card picture is {card.Image.Length:N0} bytes and {Name} takes {maxBytes:N0}, so the card has no picture."));
                return null;
            }
            var type = card.ImageContentType ?? Sniff(card.Image.Span);
            if (type is null)
            {
                notes.Add("The card picture isn't a PNG, JPEG, GIF or WebP, so the card has no picture.");
                return null;
            }
            return (card.Image.ToArray(), type);
        }
        if (card.ImageUrl is not { } url) return null;
        if (PublicAddress.Refusal(url) is { } refusal)
        {
            notes.Add($"The card picture's address was refused ({refusal}), so the card has no picture.");
            return null;
        }
        try
        {
            using var response = await Context.Http.CreateClient(SocialDefaults.MediaHttpClientName)
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                notes.Add($"The card picture couldn't be fetched ({(int)response.StatusCode}), so the card has no picture.");
                return null;
            }
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"The card picture is {response.Content.Headers.ContentLength:N0} bytes and {Name} takes {maxBytes:N0}, so the card has no picture."));
                return null;
            }
            var bytes = await ReadAtMostAsync(response.Content, maxBytes, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"The card picture is over {maxBytes:N0} bytes, which {Name} takes at most, so the card has no picture."));
                return null;
            }
            var type = response.Content.Headers.ContentType?.MediaType is { } t && t.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? t : Sniff(bytes);
            if (type is null)
            {
                notes.Add("The card picture's address didn't give a picture, so the card has no picture.");
                return null;
            }
            return (bytes, type);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            notes.Add(GuardedHttp.Blocked(e) is { } b
                ? $"The card picture's address was refused ({Clean(b.Message)}), so the card has no picture."
                : "The card picture couldn't be fetched, so the card has no picture.");
            return null;
        }
    }

    private static async Task<byte[]?> ReadAtMostAsync(HttpContent content, int max, CancellationToken ct)
    {
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + n > max) return null;
                buffer.Write(chunk, 0, n);
            }
            return buffer.ToArray();
        }
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The picture's media type from its first bytes: PNG, JPEG, GIF or WebP.</summary>
    protected static string? Sniff(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[..8].SequenceEqual(PngSignature) ? "image/png"
        : b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)) ? "image/gif"
        : b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    protected static string Extension(string contentType) => contentType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => "img",
    };

    protected static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    protected static bool IsSuccess(HttpResponseMessage r) => r.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous;

    protected static string Hash(string value, int hexChars) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..hexChars];
}
