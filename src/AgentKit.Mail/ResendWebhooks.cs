using System.Globalization;
using System.Text.Json;
using AgentKit.Channels;

namespace AgentKit.Mail;

/// <summary>What <see cref="ResendWebhooks.Verify"/> concluded about one delivery.</summary>
public enum ResendWebhookCheck
{
    /// <summary>A <c>v1</c> signature matches and the timestamp is within five minutes.</summary>
    Valid,

    /// <summary>A header is missing, empty, too long or not in the shape Svix sends.</summary>
    Malformed,

    /// <summary>The timestamp is more than five minutes from the receiver's clock: a replayed capture, or a clock far off.</summary>
    Stale,

    /// <summary>The headers are well formed and no signature matches: a forgery, or the wrong secret.</summary>
    Mismatch,
}

/// <summary>Resend's webhook event types (https://resend.com/docs/dashboard/webhooks/event-types), the email and suppression ones.</summary>
public static class ResendEventTypes
{
#pragma warning disable CS1591 // each is its own name
    public const string Sent = "email.sent";
    public const string Delivered = "email.delivered";
    public const string DeliveryDelayed = "email.delivery_delayed";
    public const string Bounced = "email.bounced";
    public const string Complained = "email.complained";
    public const string Failed = "email.failed";
    public const string Suppressed = "email.suppressed";
    public const string Opened = "email.opened";
    public const string Clicked = "email.clicked";
    public const string Scheduled = "email.scheduled";
    public const string Received = "email.received";
    public const string SuppressionAdded = "suppression.added";
    public const string SuppressionRemoved = "suppression.removed";
#pragma warning restore CS1591
}

/// <summary>
/// One Resend webhook event, read: the fields a host acts on, and <see cref="Data"/> for the rest. An event type this
/// package doesn't name still reads.
/// </summary>
/// <param name="Type">The event type, e.g. <c>email.bounced</c> (<see cref="ResendEventTypes"/>).</param>
/// <param name="CreatedAt">When Resend made the event (<c>created_at</c>), or null when it gave none that reads.</param>
/// <param name="Data">The event's <c>data</c> object, detached from the body (an empty object when there was none).</param>
public sealed record ResendWebhookEvent(string Type, DateTimeOffset? CreatedAt, JsonElement Data)
{
    /// <summary>The email the event is about (<c>data.email_id</c>, the id <see cref="EmailResult.Id"/> gave), for email events.</summary>
    public string? EmailId { get; init; }

    /// <summary>The recipients (<c>data.to</c>, a string or a list), as Resend wrote them: <c>Name &lt;address&gt;</c> stays so.</summary>
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>For <c>email.bounced</c>: <c>data.bounce.type</c>, <c>Permanent</c> for a hard bounce.</summary>
    public string? BounceType { get; init; }

    /// <summary>For <c>email.bounced</c>: <c>data.bounce.subType</c> (e.g. <c>Suppressed</c>, <c>General</c>).</summary>
    public string? BounceSubType { get; init; }

    /// <summary>For <c>email.bounced</c>: <c>data.bounce.message</c>, the receiving server's words.</summary>
    public string? BounceMessage { get; init; }

    /// <summary>For suppression events: the address (<c>data.email</c>).</summary>
    public string? Email { get; init; }

    /// <summary>For suppression events: why it was listed (<c>data.origin</c>: <c>bounce</c>, <c>complaint</c> or <c>manual</c>).</summary>
    public string? Origin { get; init; }

    /// <summary>
    /// A hard bounce: <c>email.bounced</c> with <c>data.bounce.type</c> <c>Permanent</c>. Only the hard value counts, so a
    /// missing or unknown type (Resend spells the soft one two ways) is never read as permanent.
    /// </summary>
    public bool IsHardBounce => Type == ResendEventTypes.Bounced && string.Equals(BounceType, "Permanent", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Resend's delivery webhooks. Resend sends them through Svix (https://resend.com/docs/dashboard/webhooks/verify-webhooks-requests),
/// whose scheme is the Standard Webhooks one under other header names (https://docs.svix.com/receiving/verifying-payloads/how-manual):
/// <c>svix-id</c>, <c>svix-timestamp</c> (Unix seconds) and <c>svix-signature</c> (space-separated <c>v1,</c> + base64
/// HMAC-SHA256 over <c>id.timestamp.body</c>, keyed by the <c>whsec_</c> secret's bytes). So the check is AgentKit.Channels'
/// <see cref="StandardWebhooks"/>, with its five-minute tolerance. A retry carries the same <c>svix-id</c>: it is the key a
/// host drops repeats by.
/// </summary>
public static class ResendWebhooks
{
    /// <summary>The delivery's id header.</summary>
    public const string IdHeader = "svix-id";

    /// <summary>The delivery's timestamp header.</summary>
    public const string TimestampHeader = "svix-timestamp";

    /// <summary>The delivery's signature header.</summary>
    public const string SignatureHeader = "svix-signature";

    private const int MaxId = 256;
    private const int MaxSignature = 4096;

    /// <summary>Why <paramref name="secret"/> isn't a signing secret (<c>whsec_</c> + base64), or null. Check it at startup.</summary>
    public static string? SecretRefusal(string? secret) => StandardWebhooks.SecretRefusal(secret);

    /// <summary>
    /// Checks one delivery before anything in it is read. <paramref name="body"/> is the body exactly as received; the
    /// header values are as the request carried them (a header sent twice is the host's to refuse as malformed). Every
    /// shape problem is <see cref="ResendWebhookCheck.Malformed"/>, a timestamp out of range is
    /// <see cref="ResendWebhookCheck.Stale"/> whatever it signs, and only a well-formed, fresh delivery is compared.
    /// </summary>
    /// <exception cref="ArgumentException">The secret isn't one (<see cref="SecretRefusal"/>): the host's configuration, not the delivery.</exception>
    public static ResendWebhookCheck Verify(string secret, string? id, string? timestamp, string? signature, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (SecretRefusal(secret) is { } why) throw new ArgumentException(why, nameof(secret));
        if (string.IsNullOrEmpty(id) || id.Length > MaxId || id.Any(c => c is <= ' ' or >= (char)0x7F)) return ResendWebhookCheck.Malformed;
        if (string.IsNullOrEmpty(timestamp) || timestamp.Length > 19 || !timestamp.All(char.IsAsciiDigit)) return ResendWebhookCheck.Malformed;
        if (string.IsNullOrEmpty(signature) || signature.Length > MaxSignature
            || !signature.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(s => s.StartsWith("v1,", StringComparison.Ordinal)))
            return ResendWebhookCheck.Malformed;
        if (!StandardWebhooks.IsFresh(timestamp, now)) return ResendWebhookCheck.Stale;
        return StandardWebhooks.Verify(secret, id, timestamp, signature, body, now) ? ResendWebhookCheck.Valid : ResendWebhookCheck.Mismatch;
    }

    /// <summary>
    /// Reads a verified body into an event, or null when it isn't one (not UTF-8, not JSON, not an object, no string
    /// <c>type</c>). Fields of the wrong JSON kind read as absent, never a throw.
    /// </summary>
    public static ResendWebhookEvent? Parse(ReadOnlySpan<byte> body)
    {
        // JsonDocument checks a string's UTF-8 only when the string is read, and GetString then throws: refuse it whole.
        if (!System.Text.Unicode.Utf8.IsValid(body)) return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "type") is not { Length: > 0 } type) return null;
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d.Clone() : EmptyObject();
            var bounce = data.TryGetProperty("bounce", out var b) && b.ValueKind == JsonValueKind.Object ? b : default;
            return new ResendWebhookEvent(type, Time(Str(root, "created_at")), data)
            {
                EmailId = Str(data, "email_id"),
                To = Recipients(data),
                BounceType = Str(bounce, "type"),
                BounceSubType = Str(bounce, "subType"),
                BounceMessage = Str(bounce, "message"),
                Email = Str(data, "email"),
                Origin = Str(data, "origin"),
            };
        }
    }

    private static IReadOnlyList<string> Recipients(JsonElement data)
    {
        if (!data.TryGetProperty("to", out var to)) return [];
        return to.ValueKind switch
        {
            JsonValueKind.String => [to.GetString()!],
            JsonValueKind.Array => [.. to.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)],
            _ => [],
        };
    }

    private static DateTimeOffset? Time(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
