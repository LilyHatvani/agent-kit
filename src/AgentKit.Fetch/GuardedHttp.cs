using System.Net;
using System.Net.Sockets;

namespace AgentKit.Fetch;

/// <summary>The guard refused a connection: the host isn't on the public internet, or the port isn't allowed.</summary>
public sealed class BlockedAddressException : Exception
{
    /// <summary>A refusal with no detail.</summary>
    public BlockedAddressException() : base("The address was refused.") { }

    /// <summary>A refusal that says why.</summary>
    public BlockedAddressException(string message) : base(message) { }

    /// <summary>A refusal that says why, with its cause.</summary>
    public BlockedAddressException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>How a guarded client connects.</summary>
public sealed record GuardedHttpOptions
{
    /// <summary>The ports it may connect to; null for any. A fetch of a URL someone names usually wants 80 and 443.</summary>
    public IReadOnlyCollection<int>? Ports { get; init; }

    /// <summary>Whether redirects are followed. Each hop opens its own connection through the same check.</summary>
    public bool FollowRedirects { get; init; }

    /// <summary>How many redirects are followed at most, when they are.</summary>
    public int MaxRedirects { get; init; } = 3;

    /// <summary>How long a connection may take to open.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// An HTTP handler for requests to addresses someone else chose. The check runs where it can't be dodged, at connect
/// time (<see cref="SocketsHttpHandler.ConnectCallback"/>), on the addresses DNS gave for this very connection, and the
/// socket connects to an address that was checked. A redirect, a second DNS answer (rebinding), or a name that resolves
/// to 127.0.0.1 all meet the same check. A name with any non-public address in its answer is refused whole, so a mixed
/// answer can't be used to probe which internal hosts exist. No proxy: a proxy is what the check would see instead of
/// the target. No cookies and no automatic decompression.
/// </summary>
public static class GuardedHttp
{
    /// <summary>A handler that connects only to public addresses, under <paramref name="options"/>.</summary>
    public static SocketsHttpHandler CreateHandler(GuardedHttpOptions? options = null)
    {
        var o = options ?? new GuardedHttpOptions();
        return new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = o.FollowRedirects,
            MaxAutomaticRedirections = Math.Max(1, o.MaxRedirects),
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = o.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            UseCookies = false,
            ConnectCallback = (context, ct) => ConnectAsync(context, o.Ports, ct),
        };
    }

    /// <summary>The guard's refusal behind a failed request, if the guard was what stopped it.</summary>
    public static BlockedAddressException? Blocked(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is BlockedAddressException b) return b;
        return null;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, IReadOnlyCollection<int>? ports, CancellationToken ct)
    {
        var target = context.DnsEndPoint;
        if (ports is not null && !ports.Contains(target.Port))
            throw new BlockedAddressException($"Only ports {string.Join(" and ", ports)} are allowed; this address uses {target.Port}.");
        var addresses = IPAddress.TryParse(target.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(target.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0) throw new BlockedAddressException($"{target.Host} has no address.");
        if (addresses.FirstOrDefault(a => !PublicAddress.IsPublic(a)) is { } inside)
            throw new BlockedAddressException(literal is not null
                ? $"{literal} is not on the public internet."
                : $"{target.Host} points at {inside}, which is not on the public internet.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, target.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
