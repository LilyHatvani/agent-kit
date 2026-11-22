# AgentKit.Social

Post a link to social networks from a server: Bluesky, Mastodon, Discord and X behind one contract. Built for a blog
that shares each new post, and usable by any product that does the same. No product names in the API, and nothing
stored: the host passes credentials in and keeps its own record of what was posted.

```csharp
builder.Services.AddSocialChannels();                       // the factory, a publisher, guarded HttpClients
builder.Services.AddSingleton<ISocialPostLedger, MyLedger>(); // optional: your table (in memory otherwise)

// Per post and channel, with credentials the host just opened (AgentKit.Secrets, say):
var channel = channels.Bluesky(new BlueskyAccount(handle, appPassword));
var post = new SocialPost
{
    Text = "Twelve entries, one world: how to write a lorebook that remembers.",
    Link = new Uri("https://blog.example.com/posts/lorebook?utm_source=bluesky&utm_medium=social"),
    Card = new LinkCard { Title = "How to write a lorebook", Description = "…", ImageUrl = ogImage },
    Tags = ["worldbuilding"],
    IdempotencyKey = $"blog-post:{postId}:channel:{channelId}",
};

SocialComposition draft = channel.Compose(post);              // text as sent, its length as the network counts, problems
SocialPostResult result = await publisher.PostAsync(channel, post);
// result.Posted → result.PostUrl, result.PostId (and result.Notes for anything left out)
// else result.Failure: Auth | RateLimited (RetryAfter) | Rejected (the network's message) | Transient | Unconfirmed
```

## The contract

- **`ISocialChannel`**: `Network`, a non-secret `Id`, `Idempotency` (what a retry can check), `Compose` (pure, no
  network call, for a draft editor's counter), `BeginAttempt` and `SendAsync`. `channel.PostAsync(post)` is one attempt
  without a ledger.
- **Composing.** The words, then the link on its own paragraph (unless the words hold it), then the tags not already in
  the words. A post that doesn't fit is refused with the count, never cut. Card fields that a network caps (Discord's
  embed title and description) are cut with an ellipsis.
- **The result.** Expected failures never throw: a credential refused (`Auth`), a wait (`RateLimited` with
  `RetryAfter`), the network refused this post (`Rejected`, its message trimmed to 300 characters), the network
  couldn't be reached (`Transient`, with `MayHavePosted` when the request may have been acted on), or an earlier try
  that can't be checked (`Unconfirmed`). Only the caller's own cancellation throws. Messages are safe to show and log:
  credentials are scrubbed even when a network echoes them back.
- **A post that is up but lost something** says so in `Notes` (a card picture over the network's size, a crosspost that
  failed).

## Never twice: the ledger

`SocialPublisher.PostAsync(channel, post)` stores the attempt in an `ISocialPostLedger` before sending, keyed by the
post's `IdempotencyKey`. The host backs it with its own table; `BeginAsync` must be an atomic insert that returns the
existing row on conflict (a `Failed` row is replaced).

- A key that is `Posted` answers with the earlier post (`AlreadyPosted`) and sends nothing.
- A key that is `Pending` (the answer was lost) resumes its attempt. What happens depends on the network:

| Network | Mechanism | A retry after a lost answer |
| --- | --- | --- |
| Bluesky | The record key (a TID) is chosen before sending | reads the record back (`getRecord`), then sends under the same key only if it isn't there. Any time later. |
| Mastodon | `Idempotency-Key`, kept by the instance "for up to 1 hour" | resends with the same key within 50 minutes; later, `Unconfirmed` |
| Discord bot | `nonce` + `enforce_nonce`, checked "in the past few minutes" | resends with the same nonce within 2 minutes; later, `Unconfirmed` |
| Discord webhook | none | `Unconfirmed` |
| X | none (X refuses an exact repeat of a recent post with 403, which arrives as `Rejected`) | `Unconfirmed` |

`Unconfirmed` means a person checks the network. If nothing is there, `PostAsync(channel, post, resendUnconfirmed: true)`
sends again. A refusal that created nothing (a rate limit, a credential, a validation error, a connection that never
opened) marks the attempt `Failed`, so the next call starts afresh.

## Per network

**Bluesky** (AT Protocol over HTTP, an app password).
- Session from `com.atproto.server.createSession`, kept in memory per account (createSession is rate-limited per account,
  more tightly than posting) and made again on `ExpiredToken`. `Service` defaults to `https://bsky.social`, which serves every
  Bluesky-hosted account; a self-hosted PDS sets its own.
- 300 graphemes and 3000 UTF-8 bytes. The link is shown short in the text (no scheme, at most 30 characters, as the
  Bluesky app writes it) under a link facet with the full URL; every URL and hashtag in the text gets its facet, indexed
  in UTF-8 bytes.
- The card is an `app.bsky.embed.external` with the page's title and description; its thumb is uploaded as a blob, at
  most 1,000,000 bytes. A larger or unreachable picture leaves the card without one, with a note.
- The post's address is `https://bsky.app/profile/<did>/post/<rkey>`: the DID, so it keeps working when the handle
  changes. `PostId` is the `at://` URI.

**Mastodon** (an instance and an access token with `write:statuses`, plus `write:media` for a card picture).
- `POST /api/v1/statuses` as a form, with `visibility` (`public` by default) and `language`.
- The limit is the instance's: `MaxCharacters` (default 500) and `CharactersReservedPerUrl` (default 23), both read from
  the instance's `GET /api/v2/instance` → `configuration.statuses`. Counted in graphemes, each URL at the reserved length.
- The instance builds the link card itself from the page. `AttachCardImage` uploads the picture instead
  (`POST /api/v2/media` with alt text), which replaces the card with the picture; off by default.
- The instance address must be public https.

**Discord** (an incoming webhook, or a bot in one channel).
- One message: the text (2000 characters), an embed with the card (title cut to 256, description to 4096, the link as
  the embed's URL), and `allowed_mentions: { parse: [] }`, so no text ever pings `@everyone`. Hashtags are left out.
  Without a card the link goes in the text and Discord unfurls it.
- A card picture by address goes in the embed; picture bytes go as a multipart file the embed points at.
- Webhook: `POST /webhooks/<id>/<token>?wait=true`. The URL is the credential; only `discord.com` webhook URLs are taken,
  and every request goes to `https://discord.com/api/v10`.
- Bot: `Authorization: Bot <token>`, a nonce, and `Crosspost` to publish in an announcement channel to the servers that
  follow it. The bot needs View Channel, Send Messages and Embed Links (and Manage Messages to crosspost others' posts).
- The message address needs the server id, read once from the webhook or the channel.

**X** (API v2, an OAuth 2.0 user access token with `tweet.read tweet.write users.read`).
- **Opt-in and paid.** X's API is pay-per-use credits bought upfront, with no free posting tier
  ([pricing](https://docs.x.com/x-api/getting-started/pricing), read 2026-11-22: "Post: Create" $0.015, "Post: Create
  (with URL)" $0.200 per request). Every post this package sends carries a link, so each costs the URL price. Leave the
  channel off until the owner adds a key and credits.
- 280 weighted characters (twitter-text v3: most Latin text weighs 1, CJK and emoji 2, every URL 23, after NFC).
  Only `http(s)://` URLs are counted as links; twitter-text also counts a bare domain (`example.com`) as 23, so a text
  with bare domains counts a little differently. `MaxWeightedLength` raises the limit for an X Premium account.
- X builds the card from the page; no media is uploaded.
- An access token from the PKCE flow lasts two hours. `SocialChannels.RefreshXTokenAsync(client, refreshToken)` trades
  a refresh token (`offline.access`) for a new access token. Store the refresh token the answer carries before using
  the access token: the one sent may be spent. An `Auth` failure there means the owner connects X again.
- `Id` is `x` and Mastodon's is the instance host: they don't tell two accounts on the same network apart, so keep the
  host's channel id in the idempotency key (`…:channel:<id>`).

## Credentials and egress

- **The package stores nothing.** Each account type (`BlueskyAccount`, `MastodonAccount`, `DiscordWebhook`,
  `DiscordBotChannel`, `XAccount`, `XClient`) holds its secret in memory and prints without it. Keep them sealed at rest
  with `AgentKit.Secrets` (one sealed value per channel, associated data naming the channel row) and open them just
  before making the channel.
- **HTTP.** Two named clients: `AgentKit.Social` for the networks' APIs (public addresses on port 443, no redirects)
  and `AgentKit.Social.Media` for a card picture (public addresses on 80 and 443, up to three redirects). Both go
  through `AgentKit.Fetch`'s connect-time guard, since a Mastodon instance, a PDS and a picture URL are addresses
  someone typed. Neither client logs requests: a Discord webhook's path is its token.
- **Logs.** One Information line per post (`Posted to <channel id>: <post id>`) and one Warning per failure, with the
  category and the scrubbed message.

A test replaces the primary handlers (`AddHttpClient(SocialDefaults.ApiHttpClientName).ConfigurePrimaryHttpMessageHandler(...)`);
this package's tests pin every request shape against fakes of the four networks.
