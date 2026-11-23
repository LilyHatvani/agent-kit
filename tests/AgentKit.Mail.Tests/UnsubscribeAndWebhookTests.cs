using System.Globalization;
using System.Text;
using AgentKit.Channels;

namespace AgentKit.Mail.Tests;

public sealed class UnsubscribeTests
{
    [Fact]
    public async Task The_one_click_pair_is_RFC_8058s_and_goes_out_with_the_email()
    {
        // RFC 8058 §3.1: List-Unsubscribe with one https URI in angle brackets, List-Unsubscribe-Post: List-Unsubscribe=One-Click.
        var headers = OneClickUnsubscribe.Headers(new Uri("https://auth.example.dev/api/email/unsubscribe/tok_abc?v=2"));
        Assert.Equal("<https://auth.example.dev/api/email/unsubscribe/tok_abc?v=2>", headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", headers["list-unsubscribe-post"]);
        headers["X-Entity-Ref-ID"] = "n-7";

        using var host = new TestHost();
        await host.Sender.SendAsync(new Email("a@example.dev", "b@example.com", "s") { Text = "x", Headers = headers });
        var sent = Assert.Single(host.Resend.Seen).Json.GetProperty("headers");
        Assert.Equal("<https://auth.example.dev/api/email/unsubscribe/tok_abc?v=2>", sent.GetProperty("List-Unsubscribe").GetString());
        Assert.Equal("List-Unsubscribe=One-Click", sent.GetProperty("List-Unsubscribe-Post").GetString());
        Assert.Equal("n-7", sent.GetProperty("X-Entity-Ref-ID").GetString());
    }

    [Fact]
    public void A_url_with_markup_characters_is_escaped_inside_the_brackets()
    {
        var value = OneClickUnsubscribe.Headers(new Uri("https://example.dev/u/a b<c>"))["List-Unsubscribe"];
        Assert.Equal("<https://example.dev/u/a%20b%3Cc%3E>", value);
    }

    [Theory]
    [InlineData("http://example.dev/u/1")]
    [InlineData("mailto:unsubscribe@example.dev")]
    [InlineData("https://user:pass@example.dev/u/1")]
    [InlineData("https://example.dev/u/1,2")]
    public void Anything_but_a_plain_https_url_is_refused(string url)
    {
        Assert.NotNull(OneClickUnsubscribe.UrlRefusal(new Uri(url)));
        Assert.Throws<ArgumentException>(() => OneClickUnsubscribe.Headers(new Uri(url)));
    }

    [Fact]
    public void A_relative_url_is_refused()
    {
        Assert.NotNull(OneClickUnsubscribe.UrlRefusal(new Uri("/u/1", UriKind.Relative)));
    }
}

public sealed class WebhookTests
{
    // Svix's own example (https://docs.svix.com/receiving/verifying-payloads/how-manual, "Example signatures").
    private const string SvixSecret = "whsec_plJ3nmyCDGBKInavdOK15jsl";
    private const string SvixId = "msg_loFOjxBNrRLzqYUf";
    private const string SvixTimestamp = "1731705121";
    private const string SvixSignature = "v1,rAvfW3dJ/X/qxhsaXPOyyCGmRKsaKWcsNccKXlIktD0=";
    private static readonly byte[] SvixBody = """{"event_type":"ping","data":{"success":true}}"""u8.ToArray();
    private static readonly DateTimeOffset SvixNow = DateTimeOffset.FromUnixTimeSeconds(1731705121);

    // Resend's email.bounced example (https://resend.com/docs/webhooks/emails/bounced).
    private const string Bounced = """
        {
          "type": "email.bounced",
          "created_at": "2026-11-22T23:41:12.126Z",
          "data": {
            "broadcast_id": "8b146471-e88e-4322-86af-016cd36fd216",
            "created_at": "2026-11-22T23:41:11.894Z",
            "email_id": "56761188-7520-42d8-8898-ff6fc54ce618",
            "message_id": "<111-222-333@email.example.com>",
            "from": "Acme <onboarding@resend.dev>",
            "to": ["delivered@resend.dev"],
            "subject": "Sending this example",
            "template_id": "43f68331-0622-4e15-8202-246a0388854b",
            "bounce": {
              "message": "The recipient's email address is on the suppression list because it has a recent history of producing hard bounces.",
              "subType": "Suppressed",
              "type": "Permanent"
            },
            "tags": { "category": "confirm_email" }
          }
        }
        """;

    [Fact]
    public void Svixs_published_signature_verifies()
    {
        Assert.Equal(ResendWebhookCheck.Valid, ResendWebhooks.Verify(SvixSecret, SvixId, SvixTimestamp, SvixSignature, SvixBody, SvixNow));
        // Channels signs it the same way: Resend's scheme is Standard Webhooks v1 under svix- names.
        Assert.Equal(SvixSignature, StandardWebhooks.Sign(SvixSecret, SvixId, 1731705121, SvixBody));
    }

    [Fact]
    public void Anything_changed_is_a_mismatch()
    {
        Assert.Equal(ResendWebhookCheck.Mismatch, ResendWebhooks.Verify(SvixSecret, SvixId, SvixTimestamp, SvixSignature, [.. SvixBody, 32], SvixNow));
        Assert.Equal(ResendWebhookCheck.Mismatch, ResendWebhooks.Verify(SvixSecret, "msg_other", SvixTimestamp, SvixSignature, SvixBody, SvixNow));
        Assert.Equal(ResendWebhookCheck.Mismatch, ResendWebhooks.Verify(SvixSecret, SvixId, "1731705122", SvixSignature, SvixBody, SvixNow));
        Assert.Equal(ResendWebhookCheck.Mismatch, ResendWebhooks.Verify(StandardWebhooks.NewSecret(), SvixId, SvixTimestamp, SvixSignature, SvixBody, SvixNow));
        Assert.Equal(ResendWebhookCheck.Mismatch, ResendWebhooks.Verify(SvixSecret, SvixId, SvixTimestamp, "v1,AAAA" + SvixSignature[7..], SvixBody, SvixNow));
    }

    [Fact]
    public void During_a_rotation_any_v1_signature_may_match()
    {
        var header = "v1,bm90IHRoaXMgb25l v2a,whatever " + SvixSignature;
        Assert.Equal(ResendWebhookCheck.Valid, ResendWebhooks.Verify(SvixSecret, SvixId, SvixTimestamp, header, SvixBody, SvixNow));
    }

    [Fact]
    public void A_delivery_outside_five_minutes_is_stale_whatever_it_signs()
    {
        Assert.Equal(ResendWebhookCheck.Valid, ResendWebhooks.Verify(SvixSecret, SvixId, SvixTimestamp, SvixSignature, SvixBody, SvixNow.AddMinutes(5)));
        Assert.Equal(ResendWebhookCheck.Stale, ResendWebhooks.Verify(SvixSecret, SvixId, SvixTimestamp, SvixSignature, SvixBody, SvixNow.AddMinutes(5).AddSeconds(1)));
        Assert.Equal(ResendWebhookCheck.Stale, ResendWebhooks.Verify(SvixSecret, SvixId, SvixTimestamp, SvixSignature, SvixBody, SvixNow.AddMinutes(-6)));
        Assert.Equal(ResendWebhookCheck.Stale, ResendWebhooks.Verify(SvixSecret, SvixId, "99999999999999", SvixSignature, SvixBody, SvixNow));
    }

    [Theory]
    [InlineData(null, SvixTimestamp, SvixSignature)]
    [InlineData("", SvixTimestamp, SvixSignature)]
    [InlineData("msg with space", SvixTimestamp, SvixSignature)]
    [InlineData(SvixId, null, SvixSignature)]
    [InlineData(SvixId, "", SvixSignature)]
    [InlineData(SvixId, "-1731705121", SvixSignature)]
    [InlineData(SvixId, "1731705121.0", SvixSignature)]
    [InlineData(SvixId, "17317051210000000000000", SvixSignature)]
    [InlineData(SvixId, SvixTimestamp, null)]
    [InlineData(SvixId, SvixTimestamp, "")]
    [InlineData(SvixId, SvixTimestamp, "v2a,abc")]
    [InlineData(SvixId, SvixTimestamp, "rAvfW3dJ/X/qxhsaXPOyyCGmRKsaKWcsNccKXlIktD0=")]
    public void A_header_out_of_shape_is_malformed(string? id, string? timestamp, string? signature)
    {
        Assert.Equal(ResendWebhookCheck.Malformed, ResendWebhooks.Verify(SvixSecret, id, timestamp, signature, SvixBody, SvixNow));
    }

    [Fact]
    public void Oversized_headers_are_malformed_before_any_hashing()
    {
        Assert.Equal(ResendWebhookCheck.Malformed, ResendWebhooks.Verify(SvixSecret, new string('m', 257), SvixTimestamp, SvixSignature, SvixBody, SvixNow));
        var huge = string.Join(' ', Enumerable.Repeat(SvixSignature, 100));
        Assert.Equal(ResendWebhookCheck.Malformed, ResendWebhooks.Verify(SvixSecret, SvixId, SvixTimestamp, huge, SvixBody, SvixNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("whsec_")]
    [InlineData("whsec_not base64!")]
    public void A_secret_that_isnt_one_is_the_hosts_configuration_not_the_delivery(string secret)
    {
        Assert.NotNull(ResendWebhooks.SecretRefusal(secret));
        Assert.Throws<ArgumentException>(() => ResendWebhooks.Verify(secret, SvixId, SvixTimestamp, SvixSignature, SvixBody, SvixNow));
    }

    [Fact]
    public void A_signed_bounce_verifies_and_reads()
    {
        var secret = StandardWebhooks.NewSecret();
        var body = Encoding.UTF8.GetBytes(Bounced);
        var now = DateTimeOffset.Parse("2026-11-22T23:41:13Z", CultureInfo.InvariantCulture);
        var ts = now.ToUnixTimeSeconds();
        var signature = StandardWebhooks.Sign(secret, "msg_2abc", ts, body);
        Assert.Equal(ResendWebhookCheck.Valid, ResendWebhooks.Verify(secret, "msg_2abc", ts.ToString(CultureInfo.InvariantCulture), signature, body, now));

        var e = ResendWebhooks.Parse(body)!;
        Assert.Equal(ResendEventTypes.Bounced, e.Type);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T23:41:12.126Z", CultureInfo.InvariantCulture), e.CreatedAt);
        Assert.Equal("56761188-7520-42d8-8898-ff6fc54ce618", e.EmailId);
        Assert.Equal(["delivered@resend.dev"], e.To);
        Assert.Equal("Permanent", e.BounceType);
        Assert.Equal("Suppressed", e.BounceSubType);
        Assert.StartsWith("The recipient's email address", e.BounceMessage, StringComparison.Ordinal);
        Assert.True(e.IsHardBounce);
        Assert.Equal("Sending this example", e.Data.GetProperty("subject").GetString());
    }

    [Theory]
    [InlineData("Transient")]
    [InlineData("Temporary")]
    [InlineData("Undetermined")]
    [InlineData(null)]
    public void Only_a_permanent_bounce_is_hard(string? type)
    {
        var bounce = type is null ? "{}" : $$"""{"type":"{{type}}"}""";
        var e = ResendWebhooks.Parse(Encoding.UTF8.GetBytes("""{"type":"email.bounced","data":{"to":["a@example.com"],"bounce":""" + bounce + "}}"))!;
        Assert.False(e.IsHardBounce);
    }

    [Fact]
    public void A_complaint_and_a_suppression_read()
    {
        // https://resend.com/docs/webhooks/emails/complained, https://resend.com/docs/webhooks/suppressions/added
        var complained = ResendWebhooks.Parse("""{"type":"email.complained","created_at":"2026-02-22T23:41:12.126Z","data":{"email_id":"e-1","to":"Reader <reader@example.com>"}}"""u8)!;
        Assert.Equal(ResendEventTypes.Complained, complained.Type);
        Assert.Equal(["Reader <reader@example.com>"], complained.To);
        Assert.False(complained.IsHardBounce);

        var added = ResendWebhooks.Parse("""
            {"type":"suppression.added","created_at":"2026-11-17T19:32:22.980Z",
             "data":{"id":"e169aa45-1ecf-4183-9955-b1499d5701d3","email":"steve.wozniak@gmail.com","origin":"bounce",
                     "source_id":"4ef9a417-02e9-4d39-ad75-9611e0fcc33c","created_at":"2026-11-17T19:32:22.980Z"}}
            """u8)!;
        Assert.Equal(ResendEventTypes.SuppressionAdded, added.Type);
        Assert.Equal("steve.wozniak@gmail.com", added.Email);
        Assert.Equal("bounce", added.Origin);
        Assert.Empty(added.To);
        Assert.Null(added.EmailId);
    }

    [Fact]
    public void An_event_type_this_package_doesnt_name_still_reads()
    {
        var e = ResendWebhooks.Parse("""{"type":"inbox.thread.created","data":{"inbox_id":"i-1"}}"""u8)!;
        Assert.Equal("inbox.thread.created", e.Type);
        Assert.Null(e.CreatedAt);
        Assert.Equal("i-1", e.Data.GetProperty("inbox_id").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"email.bounced\"")]
    [InlineData("{}")]
    [InlineData("""{"type":7}""")]
    [InlineData("""{"type":""}""")]
    [InlineData("""{"type":"email.sent"} trailing""")]
    public void A_body_that_isnt_an_event_is_null(string body)
    {
        Assert.Null(ResendWebhooks.Parse(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void A_body_that_isnt_UTF_8_is_null_never_a_throw()
    {
        byte[] body = [.. "{\"type\":\"email.bounced\",\"data\":{\"email_id\":\""u8.ToArray(), 0xFF, 0xFE, .. "\"}}"u8.ToArray()];
        Assert.Null(ResendWebhooks.Parse(body));
    }

    [Fact]
    public void Fields_of_the_wrong_kind_read_as_absent_and_data_outlives_the_body()
    {
        var body = Encoding.UTF8.GetBytes("""{"type":"email.bounced","created_at":5,"data":{"email_id":1,"to":[1,"a@example.com",null],"bounce":"Permanent"}}""");
        var e = ResendWebhooks.Parse(body)!;
        Array.Clear(body);
        Assert.Null(e.CreatedAt);
        Assert.Null(e.EmailId);
        Assert.Equal(["a@example.com"], e.To);
        Assert.Null(e.BounceType);
        Assert.False(e.IsHardBounce);
        Assert.Equal(3, e.Data.GetProperty("to").GetArrayLength());

        var noData = ResendWebhooks.Parse("""{"type":"email.sent","data":[1]}"""u8)!;
        Assert.Equal(System.Text.Json.JsonValueKind.Object, noData.Data.ValueKind);
    }
}
