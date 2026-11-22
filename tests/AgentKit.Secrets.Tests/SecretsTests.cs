using System.Security.Cryptography;
using System.Text;
using AgentKit.Secrets;
using Microsoft.Extensions.Configuration;

namespace AgentKit.Secrets.Tests;

public sealed class SecretsTests
{
    private static readonly MasterKeyOptions Options = new("App:Secrets", "app-master-key-id:");

    private static MasterKeys Keys(string? master, string? previous = null) => MasterKeys.From(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:Secrets:MasterKey"] = master, ["App:Secrets:PreviousKeys"] = previous })
        .Build(), Options);

    private static string Hex(byte fill) => Convert.ToHexString(Enumerable.Repeat(fill, 32).ToArray());

    [Fact]
    public void A_key_is_base64_or_64_hex_digits_and_its_id_is_the_labelled_hash_computed_independently()
    {
        var zeros = new byte[32];
        Assert.Equal("kdb1fb2db46faff6c", MasterKeys.IdOf(zeros, "relay-master-key-id:"));
        Assert.Equal("kcaf294c917bd9ce7", MasterKeys.IdOf([.. Enumerable.Range(0, 32).Select(i => (byte)i)], "agentblog-master-key-id:"));

        var fromBase64 = Keys(Convert.ToBase64String(zeros));
        var fromHex = Keys(Hex(0));
        Assert.True(fromBase64.On);
        Assert.Equal(fromBase64.CurrentId, fromHex.CurrentId);
        Assert.Equal(MasterKeys.IdOf(zeros, Options.IdLabel), fromHex.CurrentId);
        Assert.Equal("App__Secrets__MasterKey", Options.Variable);
        Assert.Equal(32, Convert.FromBase64String(MasterKeys.NewKey()).Length);
    }

    [Fact]
    public void No_key_is_off_and_a_malformed_one_or_old_keys_without_a_current_one_stop_the_start()
    {
        Assert.False(Keys(null).On);
        Assert.False(Keys("  ").On);
        Assert.Empty(Keys(null).Ids);
        var shortKey = Assert.Throws<InvalidOperationException>(() => Keys(Convert.ToBase64String(new byte[16])));
        Assert.Contains("App__Secrets__MasterKey", shortKey.Message);
        Assert.Contains("16 bytes", shortKey.Message);
        Assert.Throws<InvalidOperationException>(() => Keys("not a key"));
        Assert.Contains("App__Secrets__PreviousKeys", Assert.Throws<InvalidOperationException>(() => Keys(null, Hex(1))).Message);
        Assert.Throws<InvalidOperationException>(() => Keys(Hex(1), "bad"));
        Assert.Throws<InvalidOperationException>(() => new Sealer(Keys(null)).Seal("x", "ad"u8));
    }

    [Fact]
    public void A_sealed_value_opens_only_with_its_own_associated_data_and_unaltered()
    {
        var sealer = new Sealer(Keys(Hex(7)));
        var ad = Sealer.AssociatedData("app:v1", "channel", "42", "url");
        var v = sealer.Seal("https://hooks.example/secret", ad);
        Assert.Equal(12, v.Nonce.Length);
        Assert.Equal(sealer.Keys.CurrentId, v.KeyId);
        Assert.DoesNotContain("hooks", Encoding.UTF8.GetString(v.Ciphertext), StringComparison.Ordinal);
        Assert.Equal("https://hooks.example/secret", sealer.OpenText(v, ad));
        Assert.Equal(OpenFailure.None, sealer.Why(v, ad));

        // Another row's associated data: a value copied to another row doesn't open.
        var other = Sealer.AssociatedData("app:v1", "channel", "43", "url");
        Assert.Null(sealer.OpenText(v, other));
        Assert.Equal(OpenFailure.DoesNotOpen, sealer.Why(v, other));

        var altered = v with { Ciphertext = [.. v.Ciphertext] };
        altered.Ciphertext[0] ^= 1;
        Assert.Null(sealer.Open(altered, ad));
        Assert.Null(sealer.Open(v with { Nonce = new byte[8] }, ad));

        // Two seals of one value differ (fresh nonce).
        Assert.NotEqual(v.Ciphertext, sealer.Seal("https://hooks.example/secret", ad).Ciphertext);
    }

    [Fact]
    public void An_old_key_opens_what_it_sealed_and_a_key_out_of_the_keyring_is_named()
    {
        var ad = Sealer.AssociatedData("app:v1", "row");
        var old = new Sealer(Keys(Hex(1))).Seal("value", ad);

        var rotated = new Sealer(Keys(Hex(2), Hex(1)));
        Assert.Equal("value", rotated.OpenText(old, ad));
        Assert.NotEqual(old.KeyId, rotated.Seal("value", ad).KeyId); // new values go under the current key
        Assert.Equal(rotated.Keys.CurrentId, rotated.Keys.Ids[0]);
        Assert.Equal(2, rotated.Keys.Ids.Count);

        var forgotten = new Sealer(Keys(Hex(2)));
        Assert.Null(forgotten.OpenText(old, ad));
        Assert.Equal(OpenFailure.KeyNotInKeyring, forgotten.Why(old, ad));
        Assert.Equal(OpenFailure.NoMasterKey, new Sealer(Keys(null)).Why(old, ad));
    }

    [Fact]
    public void Associated_data_is_a_version_and_one_part_per_line_and_refuses_a_line_break()
    {
        Assert.Equal("relay-credential:v1\nowner\ngithub\nacct\ntoken"u8.ToArray(),
            Sealer.AssociatedData("relay-credential:v1", "owner", "github", "acct", "token"));
        Assert.Throws<ArgumentException>(() => Sealer.AssociatedData("v1", "a\nb"));
        Assert.Throws<ArgumentException>(() => Sealer.AssociatedData("v1", "a\rb"));
    }

    [Fact]
    public void The_format_is_AES_256_GCM_with_a_trailing_tag_so_rows_sealed_before_the_package_still_open()
    {
        // What Relay's credential store wrote before it used this package: AES-GCM, ciphertext then a 16-byte tag.
        var key = Enumerable.Repeat((byte)9, 32).ToArray();
        var ad = "relay-credential:v1\no\ni\na\nn"u8.ToArray();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = "ghp_secret"u8.ToArray();
        var output = new byte[plain.Length + 16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length), ad);

        var sealer = new Sealer(MasterKeys.From(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["R:MasterKey"] = Convert.ToBase64String(key) }).Build(), new MasterKeyOptions("R", "relay-master-key-id:")));
        Assert.Equal("ghp_secret", sealer.OpenText(new SealedValue(output, nonce, MasterKeys.IdOf(key, "relay-master-key-id:")), ad));
    }
}
