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

    /// <inheritdoc />
    public ValueTask<McpOwnerCheck> CheckAsync(McpConsentRequest request, HttpContext context, IFormCollection? form, CancellationToken cancellationToken)
    {
        var expected = secret();
        if (string.IsNullOrEmpty(expected))
            return ValueTask.FromResult(McpOwnerCheck.AskForCredential(Fields(), "Owner access is not configured on this server."));
        var given = form?[FieldName].ToString();
        if (string.IsNullOrEmpty(given)) return ValueTask.FromResult(McpOwnerCheck.AskForCredential(Fields()));
        return ValueTask.FromResult(Same(given, expected)
            ? McpOwnerCheck.Owner(OwnerId)
            : McpOwnerCheck.AskForCredential(Fields(), $"That is not the {label.ToLowerInvariant()}."));
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
