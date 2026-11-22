# AgentKit.Push

Web Push without a vendor SDK, on the BCL. Lifted from Relay (`Push/WebPush.cs`) and used by Relay and Agent Blog.

- `WebPush.Encrypt(plaintext, p256dh, auth)`: the RFC 8291 body, `aes128gcm`, one record, at most
  `WebPush.MaxPlaintext` (3993) octets of plaintext.
- `WebPush.Authorization(endpoint, vapidKeys, now)`: the RFC 8292 header, an ES256 JWT with `aud` = the endpoint's
  origin, expiring in 12 hours.
- `VapidKeys`: the pair and its `sub` contact. `Generate`, `TryParse` (base64url text) and `TryCreate` refuse what a
  push service would: not P-256, a private key that doesn't match, a subject that isn't `mailto:` or `https:`.
- `WebPush.SubscriptionRefusal(p256dh, auth)`: why a browser subscription's keys can't be used, or null.
- `WebPushReceiver.Decrypt` and `VerifyAuthorization`: the browser's and the push service's side, for tests. Written
  apart from the sender and checked against RFC 8291's worked example.

```csharp
using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
{
    Content = new ByteArrayContent(WebPush.Encrypt(payload, device.P256dh, device.Auth)),
};
request.Content.Headers.ContentEncoding.Add("aes128gcm");
request.Headers.Add("TTL", "86400");
request.Headers.TryAddWithoutValidation("Authorization", WebPush.Authorization(new Uri(endpoint), vapid, now));
```

A push endpoint is a URL a browser chose: send it through a client that only connects to public addresses
(`AgentKit.Fetch`). 404 and 410 mean the subscription is gone; 429 carries `Retry-After`.
