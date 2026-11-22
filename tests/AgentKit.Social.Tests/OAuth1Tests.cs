using System.Net;
using AgentKit.Social.Internal;

namespace AgentKit.Social.Tests;

/// <summary>
/// OAuth 1.0a against X's published worked example (docs.x.com → Authentication → OAuth 1.0a → Creating a signature).
/// The vectors are the ones Echo of Dreams' signer is pinned by; the example's tokens are marked invalid by X.
/// </summary>
public sealed class OAuth1Tests : IDisposable
{
    private static readonly OAuth1Signer.Credentials Example = new(
        ConsumerKey: "xvz1evFS4wEEPTGEFPHBog",
        ConsumerSecret: "kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Z7kBw",
        Token: "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb",
        TokenSecret: "LswwdoUaIvS8ltyTt5jkRh4J50vUPVVHtR2YPi5kE");

    private const string Nonce = "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg";
    private const long Timestamp = 1318622958;

    private static readonly KeyValuePair<string, string>[] ExampleForm =
    [
        new("status", "Hello Ladies + Gentlemen, a signed OAuth request!"),
        new("include_entities", "true"),
    ];

    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static Dictionary<string, string> ParseHeader(string header) =>
        header["OAuth ".Length..].Split(", ").Select(pair => pair.Split('=', 2))
            .ToDictionary(kv => Uri.UnescapeDataString(kv[0]), kv => Uri.UnescapeDataString(kv[1].Trim('"')));

    [Theory]
    [InlineData("https://api.x.com/1.1/statuses/update.json", "Ls93hJiZbQ3akF3HF3x1Bz8/zU4=")]
    [InlineData("https://api.twitter.com/1.1/statuses/update.json", "hCtSmYh+iHYCEqBWrE7C7hYmtUk=")]
    public void The_header_carries_the_worked_examples_signature_and_no_secret(string url, string signature)
    {
        var header = OAuth1Signer.AuthorizationHeader(HttpMethod.Post, new Uri(url), ExampleForm, Example, Nonce, Timestamp);
        var fields = ParseHeader(header);
        Assert.Equal(signature, fields["oauth_signature"]);
        Assert.Equal(Example.ConsumerKey, fields["oauth_consumer_key"]);
        Assert.Equal("HMAC-SHA1", fields["oauth_signature_method"]);
        Assert.Equal("1318622958", fields["oauth_timestamp"]);
        Assert.Equal("1.0", fields["oauth_version"]);
        Assert.DoesNotContain(Example.ConsumerSecret, header, StringComparison.Ordinal);
        Assert.DoesNotContain(Example.TokenSecret, header, StringComparison.Ordinal);
    }

    [Fact]
    public void A_query_parameter_is_signed_like_a_form_one()
    {
        var header = OAuth1Signer.AuthorizationHeader(HttpMethod.Post, new Uri("https://api.x.com/1.1/statuses/update.json?include_entities=true"),
            [ExampleForm[0]], Example, Nonce, Timestamp);
        Assert.Equal("Ls93hJiZbQ3akF3HF3x1Bz8/zU4=", ParseHeader(header)["oauth_signature"]);
    }

    [Fact]
    public async Task An_oauth1_account_signs_every_x_call_with_a_fresh_nonce_and_sends_no_bearer()
    {
        _host.Network.Respond = r => r.Uri.AbsolutePath switch
        {
            "/2/media/upload" => FakeNetwork.Json(HttpStatusCode.OK, new { data = new { id = "91" } }),
            "/2/media/metadata" => FakeNetwork.Json(HttpStatusCode.OK, new { data = new { id = "91" } }),
            _ => FakeNetwork.Json(HttpStatusCode.Created, new { data = new { id = "1" } }),
        };
        var account = new XAccount("api-key-0001", "api-key-secret-0001", "access-token-0001", "access-token-secret-0001");
        var post = new SocialPost
        {
            Text = "Hello",
            Images = [new SocialImage { Bytes = Pictures.Png(50), ContentType = "image/png", Alt = "A map" }],
            IdempotencyKey = "k",
        };
        var result = await _host.Channels.X(account).PostAsync(post);

        Assert.True(result.Posted, result.Failure?.Message);
        Assert.Equal(3, _host.Network.Seen.Count);
        var nonces = new HashSet<string>();
        foreach (var request in _host.Network.Seen)
        {
            var header = request.Header("Authorization")!;
            Assert.StartsWith("OAuth ", header, StringComparison.Ordinal);
            var fields = ParseHeader(header);
            Assert.Equal("api-key-0001", fields["oauth_consumer_key"]);
            Assert.Equal("access-token-0001", fields["oauth_token"]);
            Assert.Equal(TestHost.Start.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), fields["oauth_timestamp"]);
            Assert.True(nonces.Add(fields["oauth_nonce"]));
            Assert.DoesNotContain("secret", header, StringComparison.Ordinal);
        }
        Assert.Equal("X OAuth 1.0a (keys hidden)", account.ToString());
    }
}
