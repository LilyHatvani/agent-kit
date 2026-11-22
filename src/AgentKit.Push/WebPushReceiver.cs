using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentKit.Push;

/// <summary>
/// The receiving side, for a host's tests and tools: decrypting a push the way a browser does, and checking a VAPID
/// header the way a push service does. Written apart from <see cref="WebPush"/> (HKDF by hand on HMAC-SHA-256), and
/// checked against RFC 8291's worked example, so a test that decrypts with it isn't the sender agreeing with itself.
/// </summary>
public static class WebPushReceiver
{
    /// <summary>
    /// RFC 8291 § 3–4 receiver side, one record: the plaintext of <paramref name="message"/> for the browser key
    /// <paramref name="uaKey"/> (whose public half is <paramref name="uaPublic"/>) and <paramref name="auth"/>.
    /// Throws <see cref="CryptographicException"/> when it doesn't decrypt or isn't one well-formed record.
    /// </summary>
    public static byte[] Decrypt(byte[] message, ECDiffieHellman uaKey, byte[] uaPublic, byte[] auth)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(uaKey);
        ArgumentNullException.ThrowIfNull(uaPublic);
        ArgumentNullException.ThrowIfNull(auth);
        if (message.Length < 21) throw new CryptographicException("Too short for an aes128gcm header.");
        var salt = message[..16];
        var rs = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(16, 4));
        int idLen = message[20];
        if (idLen != 65 || message.Length < 21 + idLen + 17) throw new CryptographicException("Not a Web Push header (keyid is the sender's 65-octet key).");
        var asPublic = message[21..(21 + idLen)];
        var record = message[(21 + idLen)..];
        if (record.Length > rs) throw new CryptographicException("More than one record.");

        using var asKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] },
        });
        var ecdh = uaKey.DeriveRawSecretAgreement(asKey.PublicKey);
        var prkKey = HMACSHA256.HashData(auth, ecdh);
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic];
        byte[] ikmInfo = [.. keyInfo, 0x01];
        var ikm = HMACSHA256.HashData(prkKey, ikmInfo);
        var prk = HMACSHA256.HashData(salt, ikm);
        byte[] cekInfo = [.. "Content-Encoding: aes128gcm\0"u8, 0x01];
        byte[] nonceInfo = [.. "Content-Encoding: nonce\0"u8, 0x01];
        var cek = HMACSHA256.HashData(prk, cekInfo)[..16];
        var nonce = HMACSHA256.HashData(prk, nonceInfo)[..12];

        var cipher = record[..^16];
        var tag = record[^16..];
        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(cek, 16)) gcm.Decrypt(nonce, cipher, tag, plain);
        // Padding is zeros after the delimiter; the last record's delimiter is 0x02.
        var end = plain.Length - 1;
        while (end >= 0 && plain[end] == 0) end--;
        if (end < 0 || plain[end] != 0x02) throw new CryptographicException("The record doesn't end with the last record's 0x02 delimiter.");
        return plain[..end];
    }

    /// <summary>
    /// Checks a VAPID <c>Authorization</c> header (RFC 8292 § 2–3) the way a push service does: the ES256 signature
    /// (r ‖ s) against <c>k</c>, <c>aud</c> = the endpoint's origin, an <c>exp</c> in the future and no more than 24
    /// hours ahead of <paramref name="now"/>, and a <c>sub</c>. Returns <c>k</c> and the claims; throws
    /// <see cref="CryptographicException"/> naming what failed.
    /// </summary>
    public static (string K, JsonObject Claims) VerifyAuthorization(string authorization, Uri endpoint, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!authorization.StartsWith("vapid ", StringComparison.Ordinal)) throw new CryptographicException("Not a vapid authorization.");
        var parts = authorization["vapid ".Length..].Split(',', StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        if (!parts.TryGetValue("t", out var t) || !parts.TryGetValue("k", out var kText)) throw new CryptographicException("vapid needs t and k.");
        var jwt = t.Split('.');
        if (jwt.Length != 3) throw new CryptographicException("t is not a JWT.");
        var header = JsonNode.Parse(Base64Url.DecodeFromChars(jwt[0]));
        if (header?["alg"]?.GetValue<string>() != "ES256") throw new CryptographicException("The JWT is not ES256.");
        var k = Base64Url.DecodeFromChars(kText);
        if (k.Length != 65 || k[0] != 4) throw new CryptographicException("k is not an uncompressed P-256 key.");
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = k[1..33], Y = k[33..65] },
        });
        var sig = Base64Url.DecodeFromChars(jwt[2]);
        if (sig.Length != 64 || !verifier.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), sig, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("The JWT is not signed by k.");
        var claims = JsonNode.Parse(Base64Url.DecodeFromChars(jwt[1]))?.AsObject() ?? throw new CryptographicException("No claims.");
        if (claims["aud"]?.GetValue<string>() != endpoint.GetLeftPart(UriPartial.Authority)) throw new CryptographicException("aud is not the endpoint's origin.");
        var exp = DateTimeOffset.FromUnixTimeSeconds(claims["exp"]?.GetValue<long>() ?? 0);
        if (exp <= now || exp > now + TimeSpan.FromHours(24)) throw new CryptographicException($"exp {exp:o} is not within 24 hours of {now:o}.");
        if (string.IsNullOrEmpty(claims["sub"]?.GetValue<string>())) throw new CryptographicException("No sub contact.");
        return (kText, claims);
    }
}
