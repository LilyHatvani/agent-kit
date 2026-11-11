using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace AgentKit.McpOAuth;

/// <summary>
/// An owner check by shared secret: the first step asks for it in a password field, compared in constant time. The
/// secret is never echoed: the second step carries the page's unlock proof instead. Meant as an interim check until
/// the host has owner sign-in; replace it with an <see cref="IMcpConsentOwner"/> that reads the owner's session.
/// </summary>
/// <remarks>
/// The secret is form-posted, so keep the <c>OpenIddict</c> log category at Warning or above (the package adds that
/// filter): OpenIddict logs an authorization request's parameters at Information.
/// </remarks>
/// <param name="secret">Reads the current secret; null or empty means owner access is not configured, and nobody can consent.</param>
/// <param name="label">The field's label.</param>
/// <param name="hint">Plain text under the field (where the secret comes from), or null.</param>
public sealed class McpSecretOwnerCheck(Func<string?> secret, string label = "Owner token", string? hint = null) : IMcpConsentOwner
{
    /// <summary>The form field the secret is posted in.</summary>
    public const string FieldName = "password";

    /// <summary>The owner id this check reports.</summary>
    public const string OwnerId = "owner";

    private const int MaxTrackedAddresses = 10_000;

    private readonly Dictionary<string, (DateTimeOffset WindowEnds, int Failures)> _failures = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Wrong secrets one remote address may post within <see cref="FailedAttemptWindow"/>; past that, every attempt from it,
    /// the right secret included, is refused until the window ends. Default 10. The address is
    /// <c>HttpContext.Connection.RemoteIpAddress</c>: behind a proxy without a forwarded-headers middleware that is the
    /// proxy, and the limit is effectively global. Counted per process.
    /// </summary>
    public int MaxFailedAttempts { get; init; } = 10;

    /// <summary>The window <see cref="MaxFailedAttempts"/> counts in, from the first wrong secret. Default 15 minutes.</summary>
    public TimeSpan FailedAttemptWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <inheritdoc />
    public ValueTask<McpOwnerCheck> CheckAsync(McpConsentRequest request, HttpContext context, IFormCollection? form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var expected = secret();
        if (string.IsNullOrEmpty(expected))
            return ValueTask.FromResult(McpOwnerCheck.AskForCredential(Fields(), "Owner access is not configured on this server."));
        var given = form?[FieldName].ToString();
        if (string.IsNullOrEmpty(given)) return ValueTask.FromResult(McpOwnerCheck.AskForCredential(Fields()));

        var now = (context.RequestServices?.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System).GetUtcNow();
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "";
        if (Locked(address, now))
            return ValueTask.FromResult(McpOwnerCheck.AskForCredential(Fields(), "Too many attempts. Try again later."));
        if (Same(given, expected)) return ValueTask.FromResult(McpOwnerCheck.Owner(OwnerId));
        Fail(address, now);
        return ValueTask.FromResult(McpOwnerCheck.AskForCredential(Fields(), $"That is not the {label.ToLowerInvariant()}."));
    }

    private bool Locked(string address, DateTimeOffset now)
    {
        lock (_lock)
            return _failures.TryGetValue(Key(address, now), out var f) && f.WindowEnds > now && f.Failures >= MaxFailedAttempts;
    }

    private void Fail(string address, DateTimeOffset now)
    {
        lock (_lock)
        {
            var key = Key(address, now);
            _failures[key] = _failures.TryGetValue(key, out var f) && f.WindowEnds > now
                ? (f.WindowEnds, f.Failures + 1)
                : (now + FailedAttemptWindow, 1);
        }
    }

    /// <summary>
    /// The address's own counter, or, once <see cref="MaxTrackedAddresses"/> live windows are tracked, one counter shared
    /// by every address beyond them, so spraying addresses fails closed instead of growing the table. Under the lock.
    /// </summary>
    private string Key(string address, DateTimeOffset now)
    {
        if (_failures.ContainsKey(address) || _failures.Count < MaxTrackedAddresses) return address;
        foreach (var stale in _failures.Where(kv => kv.Value.WindowEnds <= now).Select(kv => kv.Key).ToList())
            _failures.Remove(stale);
        return _failures.Count < MaxTrackedAddresses ? address : "";
    }

    private static bool Same(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private string Fields() =>
        $"""
        <label for="{FieldName}">{WebUtility.HtmlEncode(label)}</label>
        <input id="{FieldName}" name="{FieldName}" type="password" autocomplete="current-password" required>
        {(hint is null ? "" : $"<p class=\"muted\">{WebUtility.HtmlEncode(hint)}</p>")}
        """;
}
