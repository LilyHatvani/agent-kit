using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentKit.Push;

/// <summary>
/// The Web Push wire format, by hand on the BCL: message encryption (RFC 8291, <c>aes128gcm</c> per RFC 8188, one
/// record) and the VAPID authorization (RFC 8292, an ES256 JWT). The request a host sends is a POST to the
/// subscription's endpoint with <c>Content-Encoding: aes128gcm</c>, a <c>TTL</c>, the <see cref="Authorization"/>
/// header and the <see cref="Encrypt(byte[], byte[], byte[])"/> body.
/// </summary>
public static class WebPush
{
    /// <summary>The record size written in the header: one record carries the whole message.</summary>
    public const int RecordSize = 4096;

    /// <summary>Plaintext limit: one 4096-octet record minus the 86-octet header, the delimiter and the GCM tag (RFC 8291 § 4).</summary>
    public const int MaxPlaintext = 3993;

    /// <summary>A new VAPID key pair: (public 65 octets uncompressed, private 32 octets).</summary>
    public static (byte[] Public, byte[] Private) NewKeyPair()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = k.ExportParameters(includePrivateParameters: true);
        return (Uncompressed(p.Q), p.D!);
    }

    /// <summary>
    /// Why a browser subscription's keys can't be used, or null: <c>p256dh</c> must be an uncompressed P-256 point on the
    /// curve (65 octets) and <c>auth</c> 16 octets.
    /// </summary>
    public static string? SubscriptionRefusal(byte[] p256dh, byte[] auth)
    {
        ArgumentNullException.ThrowIfNull(p256dh);
        ArgumentNullException.ThrowIfNull(auth);
        if (auth.Length != 16) return "keys.auth must be 16 octets.";
        try
        {
            using var _ = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = Point(p256dh) });
            return null;
        }
        catch (CryptographicException)
        {
            return "keys.p256dh must be an uncompressed P-256 public key.";
        }
    }

    internal static ECDsa Signer(byte[] publicKey, byte[] privateKey) => ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = Point(publicKey),
        D = privateKey,
    });

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> for the device's <paramref name="uaPublic"/> and <paramref name="authSecret"/>
    /// with a fresh ephemeral key and salt. Returns the whole body: header (salt, rs, keyid = the ephemeral public key)
    /// then one record.
    /// </summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret)
    {
        using var asKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, uaPublic, authSecret, asKey, RandomNumberGenerator.GetBytes(16));
    }

    internal static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, ECDiffieHellman asKey, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(uaPublic);
        ArgumentNullException.ThrowIfNull(authSecret);
        if (plaintext.Length > MaxPlaintext) throw new ArgumentException($"A push message is at most {MaxPlaintext} octets.", nameof(plaintext));
        var asPublic = Uncompressed(asKey.ExportParameters(false).Q);
        using var ua = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = Point(uaPublic) });
        var ecdhSecret = asKey.DeriveRawSecretAgreement(ua.PublicKey);

        // RFC 8291 § 3.4: IKM = HKDF(salt = auth_secret, ikm = ecdh_secret, info = "WebPush: info" 0x00 ua_public as_public, 32).
        var keyInfo = Concat("WebPush: info\0"u8.ToArray(), uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        // RFC 8188 § 2.2–2.3.
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, "Content-Encoding: nonce\0"u8.ToArray());

        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded, 0);
        padded[^1] = 0x02; // the last (and only) record's delimiter
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(cek, 16)) gcm.Encrypt(nonce, padded, cipher, tag);

        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return Concat(header, cipher, tag);
    }

    /// <summary>
    /// The <c>Authorization</c> header value: <c>vapid t=&lt;JWT&gt;, k=&lt;public key&gt;</c> (RFC 8292 § 3). The JWT's
    /// <c>aud</c> is the endpoint's origin; it expires 12 hours from <paramref name="now"/> (the limit is 24).
    /// </summary>
    public static string Authorization(Uri endpoint, VapidKeys keys, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(keys);
        var header = Base64Url.EncodeToString("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = (now + TimeSpan.FromHours(12)).ToUnixTimeSeconds(),
            ["sub"] = keys.Subject,
        }));
        var signingInput = $"{header}.{claims}";
        using var signer = Signer(keys.PublicKey, keys.PrivateKey);
        // IEEE P1363 (r ‖ s, 64 octets) is the JWS ES256 form, and .NET's default.
        var sig = signer.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return $"vapid t={signingInput}.{Base64Url.EncodeToString(sig)}, k={keys.PublicKeyText}";
    }

    internal static ECPoint Point(byte[] uncompressed)
    {
        if (uncompressed.Length != 65 || uncompressed[0] != 4)
            throw new CryptographicException("Not an uncompressed P-256 point.");
        return new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..65] };
    }

    private static byte[] Uncompressed(ECPoint q) => Concat([0x04], q.X!, q.Y!);

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts)
        {
            p.CopyTo(all, at);
            at += p.Length;
        }
        return all;
    }
}
