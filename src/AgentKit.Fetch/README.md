# AgentKit.Fetch

Outgoing HTTP to an address someone else chose: a picture URL an agent names, an owner's webhook, a browser's push
endpoint. The server must not become a way into the network it sits on (the database, the cloud metadata service,
anything private). Lifted from Agent Blog's media fetch and Relay's push and link guards, which now share this one
table.

- `PublicAddress.IsPublic(ip)`: refuses loopback, private, shared (100.64/10), link-local (169.254/16, metadata),
  multicast, reserved, documentation and benchmarking space; for IPv6 anything outside global unicast, plus Teredo,
  6to4 and documentation. IPv4-mapped IPv6 is judged as the IPv4 address; NAT64 is outside global unicast.
- `PublicAddress.Refusal(url, httpsOnly)`: what is certain to fail before anything is sent, said plainly.
- `GuardedHttp.CreateHandler(options)`: checks at connect time, on the addresses DNS gave for that connection, and
  connects only to a checked address. Redirects (when followed) and rebinding meet the same check. No proxy.
- `GuardedHttp.Blocked(exception)`: the refusal behind a failed request, for an honest error message.

```csharp
builder.Services.AddHttpClient("fetch")
    .ConfigurePrimaryHttpMessageHandler(() => GuardedHttp.CreateHandler(new() { Ports = [80, 443], FollowRedirects = true }));
```

A test that needs a local target replaces the primary handler; the guard itself is pinned by this package's tests.
