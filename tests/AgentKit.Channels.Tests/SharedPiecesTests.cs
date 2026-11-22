using System.Net;
using System.Text.Json.Nodes;
using AgentKit.Channels;

namespace AgentKit.Channels.Tests;

/// <summary>
/// The pieces AgentKit.Social posts with too (2026-11-22, the overlap between the two packages): one reading of a
/// Discord webhook URL, the no-ping object and the limits, one cut, one reading of a status and of <c>Retry-After</c>.
/// </summary>
public sealed class SharedPiecesTests
{
    private const string Token = "Wb7xQ-secret-webhook-token_0123456789abcdef";

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1234567890/" + Token)]
    [InlineData("https://discordapp.com/api/webhooks/1234567890/" + Token)]
    [InlineData("https://canary.discord.com/api/webhooks/1234567890/" + Token)]
    [InlineData("https://PTB.Discord.com/api/v9/webhooks/1234567890/" + Token)]
    [InlineData("https://discord.com/api/v10/webhooks/1234567890/" + Token + "?thread_id=5")]
    public void A_webhook_in_any_form_discord_hands_out_reads_to_one_canonical_address(string url)
    {
        var address = DiscordWebhookAddress.Parse(new Uri(url));
        Assert.Equal("1234567890", address.Id);
        Assert.Equal(Token, address.Token);
        Assert.Equal(new Uri($"https://discord.com/api/v10/webhooks/1234567890/{Token}"), address.Uri);
        Assert.Equal(new Uri($"https://discord.com/api/v10/webhooks/1234567890/{Token}?wait=true"), address.ExecuteUri);
        Assert.DoesNotContain(Token, address.ToString(), StringComparison.Ordinal);
        Assert.Null(Discord.WebhookRefusal(new Uri(url)));
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/+1234567890/" + Token)]          // a sign ulong.TryParse would take
    [InlineData("https://discord.com/api/webhooks/123456789012345678901/" + Token)] // past 64 bits
    [InlineData("https://discord.com/api/x10/webhooks/1234567890/" + Token)]       // not an API version
    [InlineData("https://discord.com/api/v/webhooks/1234567890/" + Token)]
    [InlineData("https://discord.com/api/webhooks/1234567890/" + Token + "/extra")]
    [InlineData("https://discord.com/webhooks/1234567890/" + Token)]
    [InlineData("https://dіscord.com/api/webhooks/1234567890/" + Token)]     // a look-alike host (Cyrillic і)
    [InlineData("https://discord.com./api/webhooks/1234567890/" + Token)]
    [InlineData("https://discord.com:443@evil.example/api/webhooks/1234567890/" + Token)]
    public void Anything_else_is_refused_and_no_refusal_repeats_the_token(string url)
    {
        Assert.False(DiscordWebhookAddress.TryParse(new Uri(url), out var address, out var refusal));
        Assert.Null(address);
        Assert.Equal(refusal, Discord.WebhookRefusal(new Uri(url)));
        var e = Assert.Throws<ArgumentException>(() => DiscordWebhookAddress.Parse(new Uri(url)));
        Assert.DoesNotContain(Token, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_payload_gets_its_own_no_ping_object_and_the_name_is_cut_to_discords_80()
    {
        var a = Discord.NoMentions();
        var b = Discord.NoMentions();
        Assert.NotSame(a, b);
        var payload = new JsonObject { ["one"] = a, ["two"] = b }; // a shared node would throw here: it has a parent already
        Assert.Empty(payload["one"]!["parse"]!.AsArray());

        var message = new ChannelMessage("t", "Title", "", null);
        using var r = Discord.Request(new Uri($"https://discord.com/api/webhooks/1/{Token}"), message, new string('n', 120));
        var body = JsonNode.Parse(await r.Content!.ReadAsStringAsync())!;
        Assert.Equal(Discord.MaxUsername, body["username"]!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData(425, SendResult.Retry)]
    [InlineData(599, SendResult.Retry)]
    [InlineData(302, SendResult.Failed)]
    [InlineData(100, SendResult.Failed)]
    [InlineData(299, SendResult.Sent)]
    [InlineData(422, SendResult.Failed)]
    public void A_status_is_read_without_its_body(int status, SendResult expected) =>
        Assert.Equal(expected, SendOutcome.Classify(status));

    [Theory]
    [InlineData("NaN", null)]
    [InlineData("Infinity", null)]
    [InlineData("1e400", null)]       // parses as infinity
    [InlineData("soon", null)]
    [InlineData("1e300", 43_200.0)]   // capped, never handed to TimeSpan.FromSeconds
    [InlineData("-5", 0.0)]
    [InlineData("1.5", 1.5)]
    [InlineData("120", 120.0)]
    public void Retry_after_is_read_finite_and_capped(string value, double? seconds)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", value);
        Assert.Equal(seconds is { } s ? TimeSpan.FromSeconds(s) : null, SendOutcome.RetryAfterOf(response, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_caller_sets_its_own_cap()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "86400");
        Assert.Equal(SendOutcome.MaxRetryAfter, SendOutcome.RetryAfterOf(response, DateTimeOffset.UnixEpoch));
        Assert.Equal(TimeSpan.FromDays(1), SendOutcome.RetryAfterOf(response, DateTimeOffset.UnixEpoch, TimeSpan.FromDays(1)));
        Assert.Equal(TimeSpan.FromHours(1), SendOutcome.RetryAfterOf(response, DateTimeOffset.UnixEpoch, TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("1e300")]
    [InlineData("1e400")]
    public async Task A_retry_after_with_nonsense_in_it_is_still_a_retry_never_a_throw(string value)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", value);
        var outcome = await SendOutcome.ReadAsync(response, DateTimeOffset.UnixEpoch, default);
        Assert.Equal(SendResult.Retry, outcome.Result);
        Assert.InRange(outcome.RetryAfter!.Value, TimeSpan.Zero, SendOutcome.MaxRetryAfter);
    }

    [Fact]
    public void A_cut_falls_between_the_characters_people_see()
    {
        Assert.Equal("…", Text.Truncate("ab", 1));
        Assert.Equal("ab", Text.Truncate("ab", 2));
        Assert.Equal("🇨🇿…", Text.Truncate("🇨🇿🇸🇰", 5));                       // never half a flag
        Assert.Equal("…", Text.Truncate("👩‍👩‍👧‍👦 family", 8));                    // a ZWJ family is one character
        Assert.Equal("café…", Text.Truncate("café au lait", 7)); // the accent stays on its letter
        Assert.Equal("caf…", Text.Truncate("café au lait", 5));
        Assert.Equal("ab…", Text.Truncate("ab cd", 4));                          // no space before the ellipsis
    }
}
