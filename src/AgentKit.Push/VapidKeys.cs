using System.Buffers.Text;
using System.Security.Cryptography;

namespace AgentKit.Push;

/// <summary>
/// The VAPID key pair a server signs its pushes with (RFC 8292): a P-256 public key, 65 octets uncompressed, its
/// private scalar, 32 octets, and the <c>sub</c> contact (<c>mailto:</c> or <c>https:</c>). A browser subscription is
/// bound to the public key: change the pair and every subscription has to be made again.
/// </summary>
public sealed record VapidKeys(byte[] PublicKey, byte[] PrivateKey, string Subject)
{
    /// <summary>The public key as base64url, what <c>pushManager.subscribe({ applicationServerKey })</c> takes.</summary>
    public string PublicKeyText => Base64Url.EncodeToString(PublicKey);

    /// <summary>The private key as base64url, for storing it.</summary>
    public string PrivateKeyText => Base64Url.EncodeToString(PrivateKey);

    /// <summary>A new pair with <paramref name="subject"/> as its contact.</summary>
    public static VapidKeys Generate(string subject)
    {
        var (pub, priv) = WebPush.NewKeyPair();
        return TryCreate(pub, priv, subject, out var reason) ?? throw new ArgumentException(reason, nameof(subject));
    }

    /// <summary>
    /// The pair from base64url text, or null with <paramref name="reason"/> saying what is wrong: not base64url, not a
    /// P-256 pair, a private key that doesn't match the public one, or a subject that isn't <c>mailto:</c> or <c>https:</c>.
    /// </summary>
    public static VapidKeys? TryParse(string publicKey, string privateKey, string? subject, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(privateKey);
        byte[] p, d;
        try
        {
            p = Base64Url.DecodeFromChars(publicKey.Trim());
            d = Base64Url.DecodeFromChars(privateKey.Trim());
        }
        catch (FormatException)
        {
            reason = "The VAPID keys are not base64url.";
            return null;
        }
        return TryCreate(p, d, subject, out reason);
    }

    /// <summary>The pair from raw octets, or null with <paramref name="reason"/>.</summary>
    public static VapidKeys? TryCreate(byte[] publicKey, byte[] privateKey, string? subject, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(privateKey);
        if (publicKey.Length != 65 || publicKey[0] != 4 || privateKey.Length != 32)
        {
            reason = "The VAPID keys are not a P-256 pair (public 65 octets uncompressed, private 32).";
            return null;
        }
        try
        {
            using var signer = WebPush.Signer(publicKey, privateKey);
            // ECDsa.Create doesn't always check that D belongs to Q; a signature that verifies against Q does.
            var probe = signer.SignData("vapid"u8, HashAlgorithmName.SHA256);
            using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = WebPush.Point(publicKey) });
            if (!verifier.VerifyData("vapid"u8, probe, HashAlgorithmName.SHA256))
            {
                reason = "The VAPID private key does not match the public key.";
                return null;
            }
        }
        catch (CryptographicException)
        {
            reason = "The VAPID private key does not match the public key.";
            return null;
        }
        if (subject is null || !(subject.StartsWith("mailto:", StringComparison.Ordinal) || subject.StartsWith("https://", StringComparison.Ordinal)))
        {
            reason = "A VAPID subject is a mailto: or https: address.";
            return null;
        }
        reason = null;
        return new VapidKeys(publicKey, privateKey, subject);
    }
}
