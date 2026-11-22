using System.Net;
using System.Net.Sockets;

namespace AgentKit.Fetch;

/// <summary>
/// The one table of what is on the public internet, for every connection to an address the server didn't choose. Two
/// guards that each kept their own table drifted apart once (Relay's link checker missed NAT64, which its push guard
/// refused; security review 2026-11-11), so there is one.
/// </summary>
public static class PublicAddress
{
    /// <summary>
    /// IPv4 that is never a public service: this network, private, shared (carrier-grade NAT, also where Railway's private
    /// network lives), loopback, link-local (169.254.169.254 is the cloud metadata service), protocol assignments, the
    /// documentation and benchmarking ranges, the 6to4 relay anycast, multicast, reserved and broadcast.
    /// </summary>
    private static readonly IPNetwork[] NotPublicV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"), IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"), IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"), IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/3"),
    ];

    /// <summary>
    /// IPv6 is public only inside global unicast (<c>2000::/3</c>), which already leaves out unspecified, loopback,
    /// IPv4-compatible, NAT64 (<c>64:ff9b::/96</c> reaches the IPv4 address embedded in it), discard, unique-local,
    /// link-local, site-local and multicast. Inside it, these are refused: Teredo and the other IETF assignments that
    /// tunnel or never route (<c>2001::/32</c>, benchmarking, ORCHID), documentation (<c>2001:db8::/32</c>,
    /// <c>3fff::/20</c>) and 6to4 (<c>2002::/16</c>, which embeds an IPv4 address).
    /// </summary>
    private static readonly IPNetwork GlobalUnicast = IPNetwork.Parse("2000::/3");

    private static readonly IPNetwork[] NotPublicV6 =
    [
        IPNetwork.Parse("2001::/32"), IPNetwork.Parse("2001:2::/48"), IPNetwork.Parse("2001:10::/28"), IPNetwork.Parse("2001:20::/28"),
        IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"), IPNetwork.Parse("3fff::/20"),
    ];

    /// <summary>Whether <paramref name="address"/> is on the public internet. An IPv4-mapped IPv6 address is judged as its IPv4 form.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !NotPublicV4.Any(n => n.Contains(address)),
            AddressFamily.InterNetworkV6 => GlobalUnicast.Contains(address) && !NotPublicV6.Any(n => n.Contains(address)),
            _ => false,
        };
    }

    /// <summary>
    /// Why <paramref name="url"/> is refused before anything is sent, or null: an absolute http(s) URL (https only when
    /// <paramref name="httpsOnly"/>), no user name or password, and no host that names a non-public address or
    /// <c>localhost</c>. A host name still meets the connect-time check (<see cref="GuardedHttp"/>); this one only says
    /// early and plainly what is certain to fail.
    /// </summary>
    public static string? Refusal(Uri url, bool httpsOnly = false)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri) return "The address must be absolute.";
        if (httpsOnly ? url.Scheme != Uri.UriSchemeHttps : url.Scheme is not ("http" or "https"))
            return httpsOnly ? "The address must be https." : "The address must be http or https.";
        if (url.UserInfo.Length > 0) return "The address must not carry a user name or password.";
        var host = url.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host.Trim('[', ']'), out var ip) && !IsPublic(ip))
            return "The address must be on the public internet.";
        return null;
    }
}
