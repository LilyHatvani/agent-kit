using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace AgentKit.Mail.Tests;

public sealed class SendTests
{
    private static Email Sample() => new("Acme <onboarding@example.dev>", "reader@example.com", "A new comment")
    {
        Text = "Someone answered you.",
        Html = "<p>Someone <b>answered</b> you.</p>",
        ReplyTo = ["support@example.dev"],
        Headers = new Dictionary<string, string> { ["X-Entity-Ref-ID"] = "c-42" },
        IdempotencyKey = "notification/42",
    };

    [Fact]
    public async Task It_posts_the_email_as_Resend_documents_it()
    {
        // https://resend.com/docs/api-reference/emails/send-email: POST https://api.resend.com/emails, Bearer key, JSON
        // body from, to (string | string[]), subject, reply_to, html, text, headers; Idempotency-Key header. User-Agent is
        // required (https://resend.com/docs/api-reference/introduction).
        using var host = new TestHost();
        var result = await host.Sender.SendAsync(Sample());

        Assert.True(result.Sent);
        Assert.Equal("49a3999c-0ce1-4ea6-ab68-afcd6dc2e794", result.Id);
        var r = Assert.Single(host.Resend.Seen);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("https://api.resend.com/emails", r.Uri.AbsoluteUri);
        Assert.Equal("Bearer " + TestHost.ApiKey, r.Header("Authorization"));
        Assert.Equal("notification/42", r.Header("Idempotency-Key"));
        Assert.StartsWith("AgentKit.Mail/", r.Header("User-Agent"), StringComparison.Ordinal);
        Assert.Equal("application/json", r.ContentType);

        var json = r.Json;
        Assert.Equal(["from", "to", "subject", "reply_to", "html", "text", "headers"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Acme <onboarding@example.dev>", json.GetProperty("from").GetString());
        Assert.Equal(["reader@example.com"], json.GetProperty("to").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("A new comment", json.GetProperty("subject").GetString());
        Assert.Equal(["support@example.dev"], json.GetProperty("reply_to").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("<p>Someone <b>answered</b> you.</p>", json.GetProperty("html").GetString());
        Assert.Equal("Someone answered you.", json.GetProperty("text").GetString());
        Assert.Equal("c-42", json.GetProperty("headers").GetProperty("X-Entity-Ref-ID").GetString());
    }

    [Fact]
    public async Task What_the_host_leaves_out_is_not_sent_and_an_empty_text_is()
    {
        using var host = new TestHost();
        await host.Sender.SendAsync(new Email("a@example.dev", ["b@example.com", "c@example.com"], "") { Html = "<p>x</p>" });
        await host.Sender.SendAsync(new Email("a@example.dev", "b@example.com", "s") { Html = "<p>x</p>", Text = "" });

        var first = host.Resend.Seen[0];
        Assert.Equal(["from", "to", "subject", "html"], first.Json.EnumerateObject().Select(p => p.Name));
        Assert.Null(first.Header("Idempotency-Key"));
        Assert.Equal(2, first.Json.GetProperty("to").GetArrayLength());
        Assert.Equal("", host.Resend.Seen[1].Json.GetProperty("text").GetString()); // Resend: "" opts out of the generated text
    }

    [Fact]
    public async Task One_email_is_always_the_same_bytes_so_a_resend_under_its_key_is_the_request_Resend_remembers()
    {
        var headersA = new Dictionary<string, string> { ["X-B"] = "2", ["X-A"] = "1" };
        var headersB = new Dictionary<string, string> { ["X-A"] = "1", ["X-B"] = "2" };
        var a = Sample() with { Headers = headersA };
        var b = Sample() with { Headers = headersB };
        Assert.Equal(ResendEmailSender.Body(a), ResendEmailSender.Body(b));

        using var host = new TestHost();
        // Resend answers a repeat of a key with the first email's id and sends nothing
        // (https://resend.com/docs/dashboard/emails/idempotency-keys).
        await host.Sender.SendAsync(a);
        var again = await host.Sender.SendAsync(b);
        Assert.True(again.Sent);
        Assert.Equal(2, host.Resend.Seen.Count);
        Assert.Equal(host.Resend.Seen[0].Body, host.Resend.Seen[1].Body);
        Assert.Equal(host.Resend.Seen[0].Header("Idempotency-Key"), host.Resend.Seen[1].Header("Idempotency-Key"));
    }

    [Fact]
    public async Task Text_that_looks_like_markup_or_JSON_goes_as_written()
    {
        using var host = new TestHost();
        var email = new Email("a@example.dev", "b@example.com", "\"quoted\" <tag> & ü 🎉") { Text = "line one\r\nline two\t\"x\"\\" };
        await host.Sender.SendAsync(email);
        var json = Assert.Single(host.Resend.Seen).Json;
        Assert.Equal(email.Subject, json.GetProperty("subject").GetString());
        Assert.Equal(email.Text, json.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_server_error_is_one_request_never_retried_inside()
    {
        using var host = new TestHost();
        host.Resend.Respond = _ => FakeResend.Json(HttpStatusCode.InternalServerError, """{"statusCode":500,"name":"application_error","message":"Unexpected"}""");
        var result = await host.Sender.SendAsync(Sample());
        Assert.False(result.Sent);
        Assert.Equal(EmailFailureKind.Transient, result.Failure!.Kind);
        Assert.True(result.Failure.MayHaveSent);
        Assert.Single(host.Resend.Seen);
    }

    public static TheoryData<string, Email> Unsendable => new()
    {
        { "no body", new Email("a@example.dev", "b@example.com", "s") },
        { "no recipient", new Email("a@example.dev", [], "s") { Text = "x" } },
        { "51 recipients", new Email("a@example.dev", [.. Enumerable.Range(0, 51).Select(i => $"r{i}@example.com")], "s") { Text = "x" } },
        { "a blank recipient", new Email("a@example.dev", " ", "s") { Text = "x" } },
        { "a line break in a recipient", new Email("a@example.dev", "b@example.com\r\nBcc: c@example.com", "s") { Text = "x" } },
        { "a line break in the reply-to", new Email("a@example.dev", "b@example.com", "s") { Text = "x", ReplyTo = ["v@example.com\nBcc: x@example.com"] } },
        { "a line break in the sender", new Email("a@example.dev\n", "b@example.com", "s") { Text = "x" } },
        { "no sender", new Email(" ", "b@example.com", "s") { Text = "x" } },
        { "a line break in a header value", new Email("a@example.dev", "b@example.com", "s") { Text = "x", Headers = new Dictionary<string, string> { ["X-A"] = "1\r\nBcc: c@example.com" } } },
        { "a colon in a header name", new Email("a@example.dev", "b@example.com", "s") { Text = "x", Headers = new Dictionary<string, string> { ["X-A: 1\r\nBcc"] = "c" } } },
        { "a space in a header name", new Email("a@example.dev", "b@example.com", "s") { Text = "x", Headers = new Dictionary<string, string> { ["X A"] = "1" } } },
        { "a header twice", new Email("a@example.dev", "b@example.com", "s") { Text = "x", Headers = new Dictionary<string, string> { ["X-A"] = "1", ["x-a"] = "2" } } },
        { "an empty key", Sample() with { IdempotencyKey = "" } },
        { "a key of 257", Sample() with { IdempotencyKey = new string('k', 257) } },
        { "a key starting with a space", Sample() with { IdempotencyKey = " notification/42" } },
        { "a key ending with a space", Sample() with { IdempotencyKey = "notification/42 " } },
        { "half a surrogate pair in the text", new Email("a@example.dev", "b@example.com", "s") { Text = "a\uD800b" } },
        { "half a surrogate pair in a header", new Email("a@example.dev", "b@example.com", "s") { Text = "x", Headers = new Dictionary<string, string> { ["X-A"] = "\uDC00" } } },
        { "a key with a line break", Sample() with { IdempotencyKey = "n/42\r\nX-Evil: 1" } },
        { "a key outside ASCII", Sample() with { IdempotencyKey = "benachrichtigung/ü" } },
    };

    [Theory]
    [MemberData(nameof(Unsendable))]
    public async Task An_email_that_cant_go_as_it_is_is_refused_before_anything_is_sent(string why, Email email)
    {
        using var host = new TestHost();
        var result = await host.Sender.SendAsync(email);
        Assert.False(result.Sent, why);
        Assert.Equal(EmailFailureKind.Rejected, result.Failure!.Kind);
        Assert.False(result.Failure.MayHaveSent);
        Assert.Null(result.Failure.Status);
        Assert.Empty(host.Resend.Seen);
    }

    [Fact]
    public async Task A_key_of_256_visible_characters_goes_as_written()
    {
        using var host = new TestHost();
        var key = "welcome-user/" + new string('9', 243);
        Assert.Equal(256, key.Length);
        Assert.True((await host.Sender.SendAsync(Sample() with { IdempotencyKey = key })).Sent);
        Assert.Equal(key, Assert.Single(host.Resend.Seen).Header("Idempotency-Key"));
    }

    [Fact]
    public async Task A_key_with_a_space_inside_goes_as_written()
    {
        using var host = new TestHost();
        Assert.True((await host.Sender.SendAsync(Sample() with { IdempotencyKey = "comment notice/42" })).Sent);
        Assert.Equal("comment notice/42", Assert.Single(host.Resend.Seen).Header("Idempotency-Key"));
    }

    [Fact]
    public async Task Whole_emoji_and_accents_are_text_and_go()
    {
        using var host = new TestHost();
        Assert.True((await host.Sender.SendAsync(new Email("a@example.dev", "b@example.com", "🎉 Café") { Text = "👩‍💻 é" })).Sent);
    }

    /// <summary>Retries what fails, as a host's ConfigureHttpClientDefaults(b => b.AddStandardResilienceHandler()) would.</summary>
    private sealed class RetryingHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null!;
            for (var i = 0; i < 4; i++)
            {
                response = await base.SendAsync(request, cancellationToken);
                if ((int)response.StatusCode < 500) break;
            }
            return response;
        }
    }

    [Fact]
    public async Task A_hosts_default_handlers_never_reach_the_Resend_client_so_one_send_is_one_request()
    {
        using var host = new TestHost(s => s.ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler(() => new RetryingHandler())));
        host.Resend.Respond = _ => FakeResend.Json(HttpStatusCode.ServiceUnavailable, "{}");
        var result = await host.Sender.SendAsync(new Email("a@example.dev", "b@example.com", "s") { Text = "no key" });
        Assert.Equal(EmailFailureKind.Transient, result.Failure!.Kind);
        Assert.Single(host.Resend.Seen);
    }

    [Fact]
    public void The_registered_client_is_the_guarded_one_no_redirect_no_proxy()
    {
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler(() => new RetryingHandler()));
        services.AddResendEmail(TestHost.ApiKey);
        using var sp = services.BuildServiceProvider();
        HttpMessageHandler handler = sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(MailDefaults.ResendHttpClientName);
        while (handler is DelegatingHandler d)
        {
            Assert.IsNotType<RetryingHandler>(d);
            handler = d.InnerHandler!;
        }
        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseProxy);
        Assert.NotNull(primary.ConnectCallback);
    }

    [Fact]
    public async Task A_key_an_answer_echoes_is_scrubbed_before_the_answer_is_cut()
    {
        using var host = new TestHost();
        // Multibyte padding puts the key across a 300-byte cut of the raw answer.
        host.Resend.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(new string('é', 130) + "Authorization: Bearer " + TestHost.ApiKey),
        };
        var result = await host.Sender.SendAsync(Sample());
        Assert.DoesNotContain("TestKey", result.Failure!.Message, StringComparison.Ordinal);
        Assert.True(result.Failure.Message.Length <= 300);
    }

    [Fact]
    public async Task An_answer_that_isnt_UTF_8_is_still_read_as_its_status()
    {
        using var host = new TestHost();
        host.Resend.Respond = _ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new ByteArrayContent([.. "{\"name\":\"validation_error\",\"message\":\""u8.ToArray(), 0xFF, 0xFE, .. "\"}"u8.ToArray()]),
        };
        var result = await host.Sender.SendAsync(Sample());
        Assert.Equal(EmailFailureKind.Rejected, result.Failure!.Kind);
        Assert.False(result.Failure.MayHaveSent);
    }

    [Fact]
    public async Task A_409_on_an_email_without_a_key_doesnt_claim_one()
    {
        using var host = new TestHost();
        host.Resend.Respond = _ => FakeResend.Json(HttpStatusCode.Conflict, """{"name":"resource_locked","message":"Another request is already updating this resource."}""");
        var result = await host.Sender.SendAsync(Sample() with { IdempotencyKey = null });
        Assert.DoesNotContain("idempotency", result.Failure!.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Statuses and names from https://resend.com/docs/api-reference/errors; the body is { statusCode, name, message } as
    // Resend's SDKs read it (resend-node src/interfaces.ts ErrorResponse).
    [Theory]
    [InlineData(401, "validation_error", "API key is invalid", EmailFailureKind.Auth, false)]
    [InlineData(401, "missing_api_key", "Missing API key in the authorization header.", EmailFailureKind.Auth, false)]
    [InlineData(403, "validation_error", "The example.dev domain is not verified.", EmailFailureKind.Auth, false)]
    [InlineData(403, "suspended_api_key", "This API key is suspended.", EmailFailureKind.Auth, false)]
    [InlineData(400, "invalid_idempotency_key", "The key must be between 1-256 chars.", EmailFailureKind.Rejected, false)]
    [InlineData(422, "validation_error", "Invalid `to` field.", EmailFailureKind.Rejected, false)]
    [InlineData(422, "missing_required_field", "Missing `subject` field.", EmailFailureKind.Rejected, false)]
    [InlineData(409, "invalid_idempotent_request", "This idempotency key has been used with a different payload.", EmailFailureKind.Rejected, true)]
    [InlineData(409, "concurrent_idempotent_requests", "There is another request in progress with the same idempotency key.", EmailFailureKind.Transient, true)]
    [InlineData(500, "application_error", "An unexpected error occurred.", EmailFailureKind.Transient, true)]
    [InlineData(503, "service_unavailable", "The API is temporarily unavailable.", EmailFailureKind.Transient, true)]
    public async Task Each_refusal_reads_as_its_category_with_Resends_message(int status, string name, string message, EmailFailureKind kind, bool mayHaveSent)
    {
        using var host = new TestHost();
        host.Resend.Respond = _ => FakeResend.Json((HttpStatusCode)status, JsonSerializer.Serialize(new { statusCode = status, name, message }));
        var result = await host.Sender.SendAsync(Sample());
        Assert.False(result.Sent);
        Assert.Equal(kind, result.Failure!.Kind);
        Assert.Equal(status, result.Failure.Status);
        Assert.Equal(mayHaveSent, result.Failure.MayHaveSent);
        Assert.Contains(name, result.Failure.Message, StringComparison.Ordinal);
        Assert.Contains(message.TrimEnd('.'), result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_409_that_says_nothing_is_retried_under_its_key_never_given_up()
    {
        using var host = new TestHost();
        host.Resend.Respond = _ => new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("<html>conflict</html>") };
        var result = await host.Sender.SendAsync(Sample());
        Assert.Equal(EmailFailureKind.Transient, result.Failure!.Kind);
        Assert.True(result.Failure.MayHaveSent);
    }

    [Theory]
    [InlineData("rate_limit_exceeded", "retry-after", "7", 7)]
    [InlineData("rate_limit_exceeded", "ratelimit-reset", "3", 3)] // seconds until the reset (https://resend.com/docs/api-reference/rate-limit)
    [InlineData("daily_quota_exceeded", "retry-after", "86400", 12 * 3600)] // capped at Channels' 12 hours
    [InlineData("monthly_quota_exceeded", "retry-after", "-5", null)] // a sign is no header
    [InlineData("rate_limit_exceeded", "ratelimit-reset", "1e9", null)]
    public async Task A_429_waits_as_long_as_Resend_said(string name, string header, string value, int? seconds)
    {
        using var host = new TestHost();
        host.Resend.Respond = _ => FakeResend.Json((HttpStatusCode)429, $$"""{"statusCode":429,"name":"{{name}}","message":"Too many requests."}""", (header, value));
        var result = await host.Sender.SendAsync(Sample());
        Assert.Equal(EmailFailureKind.RateLimited, result.Failure!.Kind);
        Assert.Equal(seconds is null ? null : TimeSpan.FromSeconds(seconds.Value), result.Failure.RetryAfter);
        Assert.Contains(name, result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_after_wins_over_the_reset_header()
    {
        using var host = new TestHost();
        host.Resend.Respond = _ => FakeResend.Json((HttpStatusCode)429, "{}", ("retry-after", "2"), ("ratelimit-reset", "9"));
        Assert.Equal(TimeSpan.FromSeconds(2), (await host.Sender.SendAsync(Sample())).Failure!.RetryAfter);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"id":""}""")]
    [InlineData("""{"id":42}""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task A_2xx_without_an_id_is_not_read_as_sent_nor_as_safe_to_send_again(string body)
    {
        using var host = new TestHost();
        host.Resend.Respond = _ => FakeResend.Json(HttpStatusCode.OK, body);
        var result = await host.Sender.SendAsync(Sample());
        Assert.False(result.Sent);
        Assert.Equal(EmailFailureKind.Transient, result.Failure!.Kind);
        Assert.True(result.Failure.MayHaveSent);
    }

    [Fact]
    public async Task A_refusal_that_isnt_JSON_keeps_its_start_on_one_line()
    {
        using var host = new TestHost();
        host.Resend.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad\r\nrequest " + new string('x', 2000)) };
        var result = await host.Sender.SendAsync(Sample());
        Assert.Equal(EmailFailureKind.Rejected, result.Failure!.Kind);
        Assert.StartsWith("Resend refused the email (400): bad request", result.Failure.Message, StringComparison.Ordinal);
        Assert.True(result.Failure.Message.Length <= 300);
        Assert.DoesNotContain('\n', result.Failure.Message);
    }

    [Fact]
    public async Task A_connection_refused_sent_nothing_a_broken_one_may_have()
    {
        using var host = new TestHost();
        host.Resend.Fail = _ => new HttpRequestException(HttpRequestError.ConnectionError, "refused");
        var refused = await host.Sender.SendAsync(Sample());
        Assert.Equal(EmailFailureKind.Transient, refused.Failure!.Kind);
        Assert.False(refused.Failure.MayHaveSent);

        host.Resend.Fail = _ => new HttpRequestException(HttpRequestError.ResponseEnded, "ended");
        Assert.True((await host.Sender.SendAsync(Sample())).Failure!.MayHaveSent);

        host.Resend.Fail = _ => new InvalidOperationException("a handler the host added");
        var odd = await host.Sender.SendAsync(Sample());
        Assert.Equal(EmailFailureKind.Transient, odd.Failure!.Kind);
        Assert.True(odd.Failure.MayHaveSent);
    }

    [Fact]
    public async Task The_deadline_running_out_is_a_timeout_that_may_have_sent()
    {
        using var host = new TestHost();
        host.Resend.Hang = true;
        var send = host.Sender.SendAsync(Sample());
        while (host.Resend.Seen.Count == 0 || host.Clock.PendingTimers == 0) await Task.Delay(5);
        host.Clock.Advance(MailDefaults.CallTimeout);
        var result = await send;
        Assert.Equal(EmailFailureKind.Transient, result.Failure!.Kind);
        Assert.True(result.Failure.MayHaveSent);
        Assert.Contains("in time", result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_callers_own_cancel_throws()
    {
        using var host = new TestHost();
        host.Resend.Hang = true;
        using var cts = new CancellationTokenSource();
        var send = host.Sender.SendAsync(Sample(), cts.Token);
        while (host.Resend.Seen.Count == 0) await Task.Delay(5);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
    }

    [Fact]
    public async Task The_key_never_reaches_a_message_or_a_log_and_a_recipient_never_reaches_a_log()
    {
        using var host = new TestHost();
        host.Resend.Respond = _ => FakeResend.Json(HttpStatusCode.Unauthorized,
            $$"""{"statusCode":401,"name":"validation_error","message":"API key {{TestHost.ApiKey}} is invalid for reader@example.com"}""");
        var result = await host.Sender.SendAsync(Sample());
        Assert.DoesNotContain(TestHost.ApiKey, result.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("***", result.Failure.Message, StringComparison.Ordinal);

        host.Resend.Respond = _ => FakeResend.Json(HttpStatusCode.OK, """{"id":"e-1"}""");
        await host.Sender.SendAsync(Sample());
        host.Resend.Fail = _ => new HttpRequestException("Authorization: Bearer " + TestHost.ApiKey);
        await host.Sender.SendAsync(Sample());

        var logs = string.Join("\n", host.Logs.Lines);
        Assert.NotEmpty(host.Logs.Lines);
        Assert.DoesNotContain(TestHost.ApiKey, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("reader@example.com", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Net.Http", logs, StringComparison.Ordinal); // RemoveAllLoggers: no request logging
        Assert.Equal("Resend (key hidden)", host.Sender.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("re_with space")]
    [InlineData("re_line\r\nX-Evil: 1")]
    [InlineData("re_ünicode")]
    public void A_key_no_header_can_carry_is_refused_at_registration_without_repeating_it(string key)
    {
        var e = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddResendEmail(key));
        if (key.Trim().Length > 0) Assert.DoesNotContain(key, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_sender_registered_first_is_kept()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmailSender, Outbox>();
        services.AddResendEmail(TestHost.ApiKey);
        using var sp = services.BuildServiceProvider();
        Assert.IsType<Outbox>(sp.GetRequiredService<IEmailSender>());
    }

    private sealed class Outbox : IEmailSender
    {
        public Task<EmailResult> SendAsync(Email email, CancellationToken cancellationToken = default) => Task.FromResult(EmailResult.Success("outbox"));
    }

    [Fact]
    public void Problems_names_every_problem_in_a_sentence()
    {
        var problems = ResendEmailSender.Problems(new Email(" ", [], "s") { IdempotencyKey = "" });
        Assert.Equal(4, problems.Count);
        Assert.All(problems, p => Assert.EndsWith(".", p, StringComparison.Ordinal));
        Assert.Empty(ResendEmailSender.Problems(Sample()));
        Assert.Contains("1 to 50", problems[1], StringComparison.Ordinal);
    }
}
