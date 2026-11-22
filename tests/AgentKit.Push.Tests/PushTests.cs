using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using AgentKit.Push;

namespace AgentKit.Push.Tests;

public sealed class PushTests
{
    // RFC 8291 § 5 and Appendix A.
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string Auth = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string Header = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string Ciphertext = "8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ";
    private const string Plaintext = "When I grow up, I want to be a watermelon";

    private static ECDiffieHellman Key(string pub, string priv)
    {
        var q = Base64Url.DecodeFromChars(pub);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.DecodeFromChars(priv),
            Q = new ECPoint { X = q[1..33], Y = q[33..65] },
        });
    }

    private static byte[] RfcMessage => [.. Base64Url.DecodeFromChars(Header), .. Base64Url.DecodeFromChars(Ciphertext)];

    [Fact]
    public void The_receiver_decrypts_the_RFC_8291_example()
    {
        using var ua = Key(UaPublic, UaPrivate);
        var plain = WebPushReceiver.Decrypt(RfcMessage, ua, Base64Url.DecodeFromChars(UaPublic), Base64Url.DecodeFromChars(Auth));
        Assert.Equal(Plaintext, Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public void The_sender_with_the_RFCs_key_and_salt_writes_the_RFCs_bytes()
    {
        using var asKey = Key(AsPublic, AsPrivate);
        var body = WebPush.Encrypt(Encoding.UTF8.GetBytes(Plaintext), Base64Url.DecodeFromChars(UaPublic), Base64Url.DecodeFromChars(Auth),
            asKey, Base64Url.DecodeFromChars(Salt));
        Assert.Equal(RfcMessage, body);
    }

    [Fact]
    public void A_fresh_message_decrypts_for_its_device_only_and_has_a_size_limit()
    {
        using var device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = device.ExportParameters(false).Q;
        byte[] pub = [0x04, .. q.X!, .. q.Y!];
        var auth = RandomNumberGenerator.GetBytes(16);
        var body = WebPush.Encrypt("""{"title":"Hi"}"""u8.ToArray(), pub, auth);
        Assert.Equal("""{"title":"Hi"}""", Encoding.UTF8.GetString(WebPushReceiver.Decrypt(body, device, pub, auth)));
        Assert.ThrowsAny<CryptographicException>(() => WebPushReceiver.Decrypt(body, device, pub, RandomNumberGenerator.GetBytes(16)));

        Assert.Equal(4096 - 86 - 1 - 16, WebPush.MaxPlaintext);
        Assert.Equal(4096, WebPush.Encrypt(new byte[WebPush.MaxPlaintext], pub, auth).Length); // 86-octet header + one full record
        Assert.Throws<ArgumentException>(() => WebPush.Encrypt(new byte[WebPush.MaxPlaintext + 1], pub, auth));
        Assert.Null(WebPush.SubscriptionRefusal(pub, auth));
        Assert.NotNull(WebPush.SubscriptionRefusal(pub, new byte[8]));
        Assert.NotNull(WebPush.SubscriptionRefusal(new byte[65], auth)); // not a point on the curve
        Assert.NotNull(WebPush.SubscriptionRefusal(pub[..33], auth));
    }

    [Fact]
    public void The_authorization_is_a_VAPID_JWT_a_push_service_accepts()
    {
        var keys = VapidKeys.Generate("mailto:owner@example.com");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var endpoint = new Uri("https://push.example.net/send/abc?x=1");
        var (k, claims) = WebPushReceiver.VerifyAuthorization(WebPush.Authorization(endpoint, keys, now), endpoint, now);
        Assert.Equal(keys.PublicKeyText, k);
        Assert.Equal("https://push.example.net", claims["aud"]!.GetValue<string>());
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), claims["exp"]!.GetValue<long>());
        Assert.Equal("mailto:owner@example.com", claims["sub"]!.GetValue<string>());

        // Another endpoint's origin, a day later, or another key's signature: refused.
        Assert.Throws<CryptographicException>(() => WebPushReceiver.VerifyAuthorization(WebPush.Authorization(endpoint, keys, now), new Uri("https://other.example/x"), now));
        Assert.Throws<CryptographicException>(() => WebPushReceiver.VerifyAuthorization(WebPush.Authorization(endpoint, keys, now), endpoint, now.AddDays(1)));
        var forged = WebPush.Authorization(endpoint, VapidKeys.Generate("mailto:x@example.com"), now);
        var swapped = forged[..forged.IndexOf(", k=", StringComparison.Ordinal)] + ", k=" + keys.PublicKeyText;
        Assert.Throws<CryptographicException>(() => WebPushReceiver.VerifyAuthorization(swapped, endpoint, now));
    }

    [Fact]
    public void VAPID_keys_are_checked_as_a_push_service_would()
    {
        var a = VapidKeys.Generate("https://blog.example");
        Assert.NotNull(VapidKeys.TryParse(a.PublicKeyText, a.PrivateKeyText, "https://blog.example", out var none));
        Assert.Null(none);

        Assert.Null(VapidKeys.TryParse("not base64url!", a.PrivateKeyText, "mailto:a@b.c", out var r1));
        Assert.Contains("base64url", r1);
        Assert.Null(VapidKeys.TryCreate(a.PublicKey[..33], a.PrivateKey, "mailto:a@b.c", out var r2));
        Assert.Contains("P-256", r2);
        var b = VapidKeys.Generate("mailto:a@b.c");
        Assert.Null(VapidKeys.TryCreate(a.PublicKey, b.PrivateKey, "mailto:a@b.c", out var r3));
        Assert.Contains("does not match", r3);
        Assert.Null(VapidKeys.TryCreate(a.PublicKey, a.PrivateKey, "http://insecure.example", out var r4));
        Assert.Contains("mailto:", r4);
        Assert.Null(VapidKeys.TryCreate(a.PublicKey, a.PrivateKey, null, out _));
    }
}
