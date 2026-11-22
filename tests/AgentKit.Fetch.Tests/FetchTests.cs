using System.Net;
using AgentKit.Fetch;

namespace AgentKit.Fetch.Tests;

public sealed class FetchTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("100.128.0.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("2a00:1450:4001::200e", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.255.255.254", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.0.0.8", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("192.88.99.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("::a9fe:a9fe", false)]          // IPv4-compatible
    [InlineData("64:ff9b::a9fe:a9fe", false)]   // NAT64 reaches 169.254.169.254
    [InlineData("64:ff9b:1::1", false)]
    [InlineData("100::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2001:0:4136:e378::1", false)]  // Teredo
    [InlineData("2001:2::1", false)]
    [InlineData("2001:10::1", false)]
    [InlineData("2002:a00:1::", false)]          // 6to4 of 10.0.0.1
    [InlineData("3fff::1", false)]
    public void The_one_table_of_public_addresses(string address, bool isPublic) =>
        Assert.Equal(isPublic, PublicAddress.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://hooks.example.com/x", false, null)]
    [InlineData("http://hooks.example.com/x", false, null)]
    [InlineData("http://hooks.example.com/x", true, "https")]
    [InlineData("ftp://hooks.example.com/x", false, "http or https")]
    [InlineData("https://user:pass@hooks.example.com/x", false, "user name")]
    [InlineData("https://localhost/x", false, "public internet")]
    [InlineData("https://a.localhost./x", false, "public internet")]
    [InlineData("https://127.0.0.1/x", false, "public internet")]
    [InlineData("https://[::1]/x", false, "public internet")]
    [InlineData("https://[::ffff:192.168.1.1]/x", false, "public internet")]
    [InlineData("https://169.254.169.254/latest/meta-data", false, "public internet")]
    public void A_url_certain_to_fail_is_refused_before_anything_is_sent(string url, bool httpsOnly, string? reason)
    {
        var refusal = PublicAddress.Refusal(new Uri(url), httpsOnly);
        if (reason is null) Assert.Null(refusal);
        else Assert.Contains(reason, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:{port}/")]
    [InlineData("http://localhost:{port}/")]
    [InlineData("http://[::1]:{port}/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://0.0.0.0:{port}/")]
    public async Task The_guarded_client_never_connects_to_a_non_public_address(string template)
    {
        // A real listener on loopback: if the guard let the connection through, the request would be answered.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var http = new HttpClient(GuardedHttp.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };
            var e = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri(template.Replace("{port}", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))));
            Assert.NotNull(GuardedHttp.Blocked(e));
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Ports_outside_the_allowed_ones_are_refused_before_dns()
    {
        using var http = new HttpClient(GuardedHttp.CreateHandler(new() { Ports = [80, 443] }));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri("https://8.8.8.8:8443/x.png")));
        Assert.Contains("8443", GuardedHttp.Blocked(e)!.Message, StringComparison.Ordinal);
        Assert.Null(GuardedHttp.Blocked(new InvalidOperationException("x")));
    }
}
