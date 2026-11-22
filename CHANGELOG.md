# Changelog

Per package, newest first. Versions are `0.1.<CI run number>`; an entry names the first version that carries it.

## AgentKit.Mail

### Unreleased

- **First release**, for Agent Blog's email notifications and agent-support, shaped from what Echo of Dreams sends
  today. Built on AgentKit.Channels (the reading of a status and of `Retry-After`, the cut, the Standard Webhooks
  verifier) and AgentKit.Fetch (the guarded client). No Resend SDK.
  - `Email` (from, to, subject, text, HTML, reply-to, headers, idempotency key) and `IEmailSender`;
    `ResendEmailSender` posts it to `POST https://api.resend.com/emails` once, as the same bytes every time, with the
    `Idempotency-Key` and the `User-Agent` Resend requires. `AddResendEmail(apiKey)` registers it and its client
    (public addresses on 443, no redirect, no request logging, a 64 KiB answer cap), unless the host registered its own
    sender first.
  - `EmailResult`: sent with Resend's id, or failed as `Auth`, `RateLimited` (with the wait), `Rejected` (Resend's
    message, trimmed) or `Transient` (with `MayHaveSent`). Expected failures never throw; the API key never reaches a
    message or a log, and logs carry no address.
  - `OneClickUnsubscribe.Headers(url)`: the RFC 8058 `List-Unsubscribe` / `List-Unsubscribe-Post` pair.
  - `ResendWebhooks.Verify` (Svix headers: `Valid`, `Malformed`, `Stale`, `Mismatch`) and `Parse` into
    `ResendWebhookEvent` (type, email id, recipients, bounce, suppression origin, `IsHardBounce`, the `data` object).
  - No usage caps: only what Resend refuses, plus what can't be put on the wire unchanged.

## AgentKit.Social

### Unreleased

- **Built on AgentKit.Channels** (a package dependency now). Channels and Social both send Discord and Slack webhooks,
  and what they share now lives once, in Channels:
  - Discord: the webhook URL is read by `DiscordWebhookAddress` (the API base too), and the payload takes
    `Discord.NoMentions()` and the `Discord.Max*` limits.
  - Slack: the URL is checked by `Slack.WebhookRefusal`, and the text is escaped with `Slack.Escape`.
  - Every cut (card fields, the webhook `Username`, now to Discord's 80, and a failure's message) is `Text.Truncate`.
  - Every network's refusal is read with `SendOutcome.Classify`, `RetryAfterOf` and `Bounded`, Discord's and
    Telegram's body `retry_after` included.
  - `TextRules.Cut` and the internal `DiscordWebhookUrl` are gone.
  - Changed on that reading, each to the stricter or the correct side:
    - A 408 or 425 is `Transient` (the network didn't act on it), not `Rejected`.
    - Discord's 410 is `Auth`, like its 404. Off Discord and Slack, a 404 or 410 is still `Rejected`.
    - A Discord webhook id with a sign (`+1`) or past 64 bits, or an API segment other than `v<n>`, is refused.
    - A `Retry-After` with a fraction is read, and it now goes before the reset headers (1.5 s where
      `X-RateLimit-Reset-After: 30` gave 30 s), as a whole-seconds one always did. One with a sign or an exponent is
      ignored, so the network's reset header or body still decides. The reset headers (`X-RateLimit-Reset-After` and
      the epoch ones) are read just as strictly, so a malformed one leaves the wait to the body's `retry_after`
      (Discord, Telegram).
    - A body `retry_after` of a day or more waits a day; it was ignored before.
  - Changed, and named here so nothing is a surprise:
    - The Discord host is compared as its IDN form, so a full-width `ｄiscord.com` reads as `discord.com`. The request
      still goes only to `https://discord.com/api/v10`.
    - A cut drops the whitespace before its "…".
    - A failure message's cut never splits an emoji.
    - The refusal texts for a bad webhook URL are Channels' wording.
- **Slack and Telegram.** `SocialChannels.Slack(SlackWebhook)`: one escaped message whose link Slack previews itself;
  pictures refused (a webhook can't attach them); no message id comes back, so the result has none and says so.
  `SocialChannels.Telegram(TelegramChat)`: a bot posting in a channel, group or chat, by `@username` or chat id; plain
  text with a link preview, or pictures as a photo, an album or an animation with the text as the caption, Bot API
  limits checked in `Compose`; the post's `t.me` address. Neither network can be asked about a lost answer
  (`Unconfirmed`). Both carry their credential in the request URL, kept out of ids, messages and logs.
- **First release.** One `ISocialChannel` contract and adapters for Bluesky (AT Protocol, app password, link and tag
  facets in UTF-8 bytes, an images embed or an external card), Mastodon (instance token, `Idempotency-Key`, the
  instance's own limits, media processing polled), Discord (incoming webhook or bot, attachments or an embed with
  colour, footer and time, no mentions parsed, bot nonce and crosspost) and X (API v2 in user context, OAuth 1.0a
  signed or an OAuth 2.0 token with refresh, media upload with alt text, a weighted count never under X's own; needs a
  paid X API plan). The Bluesky, X and Discord adapters carry over Echo of Dreams' announcement cross-posters; the
  README says what changed and why.
- **Pictures** (`SocialPost.Images`, alt text required) post as each network's own media; per-network count, size,
  format and animated-GIF limits are refused in `Compose`, never dropped. A picture that fails halfway posts nothing
  and the failure says what became of the ones already uploaded (Mastodon's are deleted).
- `ISocialPostLedger.SaveAsync` is conditional (same attempt, still pending), so a late refusal from one caller never
  overwrites a post another caller landed.
- No usage limits of its own: only what the network refuses. Resource bounds sized to the networks' caps: a deadline
  per call on the host's clock (uploads get time for their bytes), a 1 MiB cap on an answer, bounded media polling.
- `SocialPublisher.PostEachAsync(postKey, deliveries)` posts one item to a set of channels at once and answers per
  channel; each key is `KeyFor(postKey, channelKey)`, so a rerun of the set skips what is up.
- `Compose` gives the text as sent, its length as the network counts it and what stops it; a post that doesn't fit is
  refused, never cut. Results are posted (the network's id and URL, plus notes on anything left out) or failed with a
  category (`Auth`, `RateLimited` with `RetryAfter`, `Rejected` with the network's message, `Transient` with
  `MayHavePosted`, `Unconfirmed`); expected failures never throw.
- `SocialPublisher` with a host-backed `ISocialPostLedger`: a posted key answers with the earlier post; a lost answer
  resumes the attempt (Bluesky reads its pre-chosen record key back, Mastodon and a Discord bot resend inside the
  network's duplicate window, the others answer `Unconfirmed` until a person resends).
- Credentials come from the host and print hidden; messages and logs are scrubbed of them. Both HttpClients go through
  AgentKit.Fetch's public-address guard and log no requests.

## AgentKit.Channels

### Unreleased

- **The lower layer for AgentKit.Social's Discord and Slack channels.** Additive only: nothing public was removed or
  re-shaped. New:
  - `DiscordWebhookAddress`: `TryParse`, `Parse`, `Id`, `Token`, the canonical `Uri` and `ExecuteUri` on `Api`
    (`https://discord.com/api/v10`). It prints without the token.
  - `Discord.NoMentions()`, and the limits `Discord.MaxContent`, `MaxFooter`, `MaxAttachmentDescription` and
    `MaxUsername`.
  - `SendOutcome.Classify(status)`, `SendOutcome.RetryAfterOf(response, now, max)` and `SendOutcome.Bounded(wait, max)`.
  - `Discord.Request` and `Slack.Request` still post to the URL they are given.
- **Fixed:**
  - `StandardWebhooks.Verify` threw `ArgumentOutOfRangeException` on a `webhook-timestamp` past the year 9999
    (`99999999999999`): it built a `DateTimeOffset` from it. The timestamp is compared as seconds now, so such a
    delivery is stale and refused. New, for receivers: `StandardWebhooks.IsFresh(timestamp, now)` (the same check on its
    own) and `StandardWebhooks.SecretRefusal(secret)` (check a secret at startup instead of on the first delivery).
  - A `Retry-After` of `NaN`, `Infinity` or `1e300` made `SendOutcome.ReadAsync` throw (it reached
    `TimeSpan.FromSeconds`). Only whole seconds, seconds with a fraction or a date are read now; anything else is no
    header, so a 429 waits the default minute. That covers a negative value too, which used to mean "send again now".
  - `Text.Truncate(text, 1)` threw on text of two or more characters; it now gives "…".
  - `Text.Truncate` cuts between grapheme clusters, so it never splits a flag, a ZWJ emoji or a letter from its accent
    (it already kept surrogate pairs whole).
- **Stricter, for URLs no service hands out:**
  - `Discord.WebhookRefusal` refuses a webhook id with a sign or past 64 bits, and an API segment other than `v<n>`
    (`/api/x10/webhooks/…`).
  - `Slack.WebhookRefusal` refuses a query or a fragment, with a refusal that says so. A wrong host or path keeps the
    old text.
- README: a Discord or Slack webhook's path is its token. Send through a client with `.RemoveAllLoggers()`, and keep
  HttpClient's trace spans for those hosts out of exports.
- **First release**, for Agent Blog's notifications: Discord and Slack webhooks, ntfy and Standard Webhooks signed
  webhooks (sign pinned to the spec's reference vector, verify for receivers). Each builder escapes agent-written text
  for its destination; `SendOutcome` reads an answer as sent, retry (with `Retry-After`), gone or failed; `Retries` is
  1 m, 5 m, 30 m, 2 h, 12 h.

## AgentKit.Secrets

### Unreleased

- **First release**, lifted from Relay's credential store (its decision 0024). A keyring from configuration
  (`<Section>:MasterKey`, `<Section>:PreviousKeys`; base64 or 64 hex digits), key ids that open nothing (`k` + 16 hex of
  SHA-256 over the host's label and the key), and AES-256-GCM sealing with a fresh nonce and associated data naming the
  row. `Sealer.Why` says whether a value failed for no master key, a key out of the keyring, or a row that doesn't open.
  The format is Relay's (ciphertext then tag), so rows Relay sealed before keep opening.

## AgentKit.Push

### Unreleased

- **First release**, lifted from Relay's `Push/WebPush.cs`. RFC 8291 encryption (pinned to the RFC's own bytes), the
  RFC 8292 VAPID header, `VapidKeys` with the checks a push service makes, `WebPush.SubscriptionRefusal`, and
  `WebPushReceiver` (decrypt, verify VAPID) for tests.

## AgentKit.Fetch

### Unreleased

- **First release.** One table of public addresses (`PublicAddress.IsPublic`), merged from Relay's address guard and
  Agent Blog's media fetch: IPv6 is public only inside global unicast, so NAT64 and IPv4-compatible addresses are
  refused; Teredo, 6to4 and both documentation ranges are refused too. `GuardedHttp.CreateHandler` checks at connect
  time on the addresses DNS gave for that connection, optionally limits ports and follows redirects through the same
  check; `PublicAddress.Refusal` says early what is certain to fail.

## AgentKit.ProviderSignIn

### Unreleased

- **First release.** Server-driven Google and Discord sign-in, the server as relying party:
  - authorization code with PKCE S256, and one registered redirect per provider, built from `PublicUrl`;
  - a single-use state (10 min, stored hashed), bound to the browser by a `__Host-` cookie, and pinned to its provider;
  - Google's ID token checked for RS256 signature, issuer, audience, expiry and this sign-in's nonce; Discord's user id
    read from `/users/@me`;
  - no provider tokens kept;
  - each provider enabled only with its client id and secret, and listed at `GET /api/auth/providers`.
- **Host contract.** The host implements `IProviderSignInCompletion` and receives a verified `(provider, account id)`
  plus display hints. It decides who that is; the package never matches by email. `ProviderSignInFlow.BeginAsync` lets
  the host begin a sign-in with its own purpose (setup, bind), kept server-side.
- **State store.** In memory by default, or PostgreSQL (`ConnectionString`, schema from `ProviderSignInSchema.ReadScript()`).

## AgentKit.McpOAuth

### Unreleased

Fixes from Relay's security review of 2026-11-11. No schema change and no option is required; behaviour changes are
marked.

- **Loopback redirects are matched on their literal host.** An `http` redirect counts as loopback (any port) only when
  written as `127.0.0.1`, `[::1]` or `localhost` (RFC 8252 § 8.3), with no userinfo, whitespace or control characters,
  for seeded clients and metadata documents alike. Before, anything .NET maps to loopback passed, including
  `http://loopback:8080/…`, a name the browser looks up. *Behaviour change:* `127.1`, `localhost.` and `user@localhost`
  redirects are now refused.
- **A metadata document client id must be canonical.** It is fetched and stored only when `client_id ==
  url.AbsoluteUri`; any other URL-shaped client id (`:443`, dot segments, upper case) gets 400 at `/oauth/authorize`,
  so a row an earlier version stored under an alias is never used.
- **Metadata documents are cached.** A stored document is not fetched again for `MetadataDocumentCacheLifetime`
  (5 min), a missing or refused one for `MetadataDocumentFailureCacheLifetime` (1 min); at most
  `MetadataDocumentCacheSize` (1000) URLs, oldest out first. Per process.
- **Optional rate limit on `/oauth/authorize`.** `AuthorizeRateLimiter` takes a `PartitionedRateLimiter<HttpContext>`,
  applied to GET and POST before any fetch or owner check; refused requests get 429 with `Retry-After`.
- **The consent unlock is single-use and bound to the browser.** Allow spends the unlock's nonce in the token table's
  unique `ReferenceId` index, so a double submit or replay (on any replica) gets the page again instead of a second
  grant. The unlock is also bound to a random `__Host-agentkit-mcp-consent` cookie (HttpOnly, SameSite=Strict) the page
  sets, and its MAC now covers `scope`, `response_type` and `code_challenge_method`, over a length-prefixed encoding
  instead of `\n`-joined fields. *Behaviour change:* the consent page needs cookies, and a consent page open across
  the upgrade must be confirmed again.
- **`McpSecretOwnerCheck` limits wrong secrets.** 10 per remote address per 15 minutes (`MaxFailedAttempts`,
  `FailedAttemptWindow`); past that every attempt from the address is refused until the window ends. *Behaviour
  change, on by default:* behind a proxy without a forwarded-headers middleware every request shares the proxy's
  address, so anyone can hold the owner's consent page shut with 10 wrong posts per 15 minutes (existing connections
  are unaffected). Set `MaxFailedAttempts = int.MaxValue` to turn it off.

### 0.1.4

- **First release.** An MCP authorization server on OpenIddict 7.8:
  - authorization code with PKCE S256 only, for public clients;
  - hashed reference access tokens (60 min) and refresh tokens (30 days), with strict rotation: reusing a refresh token
    revokes the grant;
  - audience pinned to `<issuer>/mcp`, and RFC 8707 resource indicators checked at both endpoints;
  - RFC 9728 discovery, RFC 9207 `iss`, and `WWW-Authenticate` with `resource_metadata`;
  - seeded clients with any-port loopback redirects, and client ID metadata documents from allowlisted hosts;
  - a POST-only consent page with an HMAC unlock;
  - uncached per-call revocation;
  - issuer pinning behind a proxy;
  - keys stored in the database.
- **Host contracts.** The host implements `IMcpConnectionStore` and `IMcpConsentOwner`. `McpSecretOwnerCheck` is a
  ready owner check by shared secret.
- **Schema.** Shipped as an embedded script (`McpOAuthSchema.ReadScript()`) for the host's own migrator.
