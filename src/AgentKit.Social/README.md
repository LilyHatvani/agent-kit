# AgentKit.Social

Post to social networks from a server: Bluesky, Mastodon, Discord and X behind one contract. Pictures go as each
network's own media; a link card is the fallback for a post without pictures. Built for a product that shares what it
publishes (a blog post, an announcement), with no product names in the API. Nothing is stored: the host passes
credentials in and keeps its own record of what was posted.

```csharp
builder.Services.AddSocialChannels();                       // the factory, a publisher, guarded HttpClients
builder.Services.AddSingleton<ISocialPostLedger, MyLedger>(); // optional: your table (in memory otherwise)

// Per item and channel, with credentials the host just opened (AgentKit.Secrets, say):
var channel = channels.Bluesky(new BlueskyAccount(handle, appPassword));
var post = new SocialPost
{
    Text = "Twelve entries, one world: how to write a lorebook that remembers.",
    Link = new Uri("https://blog.example.com/posts/lorebook?utm_source=bluesky&utm_medium=social"),
    Images = [new SocialImage { Bytes = cover, ContentType = "image/png", Alt = "A hand-drawn map of a coastline" }],
    Tags = ["worldbuilding"],
    IdempotencyKey = SocialPublisher.KeyFor($"blog-post:{postId}", channelRow.Id),
};

SocialComposition draft = channel.Compose(post);              // text as sent, its length as the network counts, problems
SocialPostResult result = await publisher.PostAsync(channel, post);
// result.Posted → result.PostUrl, result.PostId (and result.Notes for anything left out)
// else result.Failure: Auth | RateLimited (RetryAfter) | Rejected (the network's message) | Transient | Unconfirmed
```

**Posting to the channels a person ticked.** One call, one post written per network, one result per channel:

```csharp
var results = await publisher.PostEachAsync($"announcement:{id}",
[
    new SocialDelivery(blueskyRow.Id, channels.Bluesky(blueskyAccount), blueskyDraft),
    new SocialDelivery(xRow.Id, channels.X(xAccount), xDraft),
    new SocialDelivery(discordRow.Id, channels.Discord(webhook), discordDraft),
]);
foreach (var r in results) Record(r.ChannelKey, r.Result);   // in the order given; one failure never stops another
```

Each delivery's key is `SocialPublisher.KeyFor(postKey, channelKey)` (`announcement:42:channel:<row id>`), so running
the same set again skips the channels that are up and resumes or retries the rest. The channels post at the same time.

## The contract

- **`ISocialChannel`**: `Network`, a non-secret `Id`, `Idempotency` (what a retry can check), `Compose` (pure, no
  network call, for a draft editor's counter), `BeginAttempt` and `SendAsync`. `channel.PostAsync(post)` is one attempt
  without a ledger.
- **Composing.** The words, then the link on its own paragraph (unless the words hold it), then the tags not already in
  the words. A post that doesn't fit is refused with the count, never cut, and so is a picture the network won't take
  (too many, too large, a format it refuses, no alt text, an animated GIF where it can't go). Card fields that a
  network caps (Discord's embed title and description) are cut with an ellipsis.
- **Pictures** (`SocialPost.Images`, 0 to n, each with bytes, a PNG, JPEG, GIF or WebP type, and alt text, which is
  required). They upload as the network's own media before the post. With pictures, no link card is sent; the link
  stays in the text.
- **The result.** Expected failures never throw: a credential refused (`Auth`), a wait (`RateLimited` with
  `RetryAfter`, at most a day), the network refused this post (`Rejected`, its message trimmed to 300 characters), the
  network couldn't be reached or answered in a shape nobody expected (`Transient`, with `MayHavePosted` when the
  request may have been acted on), or an earlier try that can't be checked (`Unconfirmed`). Only the caller's own
  cancellation throws, and only before the post is up: an answer that arrived is kept. Messages are safe to show and
  log: credentials are scrubbed even when a network echoes them back.
- **A post that is up but lost something** says so in `Notes` (a card picture over the network's size, a crosspost that
  failed).

## Pictures, per network

| Network | How | Per post | Each picture | Animated GIF | Alt text |
| --- | --- | --- | --- | --- | --- |
| Bluesky | `uploadBlob`, then an `app.bsky.embed.images` embed (with `aspectRatio` when `SocialImage.Width` and `Height` are given) | 4 | 2,000,000 bytes | posted, with a note that it shows still (from recall: not stated in the lexicon) | required by the lexicon |
| Mastodon | `POST /api/v2/media` (polls `GET /api/v1/media/:id` while it answers 206), then `media_ids[]` | `MaxMediaAttachments` (4) | `ImageSizeLimit` (16 MiB) | becomes a gifv, beside pictures too, up to `VideoSizeLimit` (99 MiB) | 1500 |
| X | `POST /2/media/upload` (`tweet_image` or `tweet_gif`, polls the STATUS command while processing), `POST /2/media/metadata`, then `media.media_ids` | 4 | 5,000,000 bytes | alone, up to 15,000,000 bytes | 1000 |
| Discord | files on the message itself (`files[n]`, `attachments[]`), with `SUPPRESS_EMBEDS` so the link doesn't unfurl into a card | 10 | 20 MiB, 25 MiB a message | plays among other pictures | 1024 |

Mastodon's numbers are the defaults; set them from the instance's `GET /api/v2/instance` (`configuration.statuses`,
`configuration.media_attachments`). Discord's 10 files a message is its long-standing limit, not stated on the pages
cited.

**A picture that fails halfway** stops the post: nothing goes out with a picture missing. The failure says which
picture and why, and what became of the ones already uploaded:
- Bluesky: blobs no post uses are deleted by the PDS on its own (the `uploadBlob` lexicon).
- Mastodon: the package deletes them (`DELETE /api/v1/media/:id`, Mastodon 4.4 and later). On an older instance they
  stay unattached, which shows them to no one, and the message says how many.
- X: there is no delete; an unused upload expires (`expires_after_secs`).
- Discord: the pictures travel in the one request with the message, so there is nothing to clean up.

A status refused outright after its pictures uploaded deletes them too (Mastodon). A resend under the same key that
answers with the first try's status deletes the resend's own uploads.

## Never twice: the ledger

`SocialPublisher.PostAsync(channel, post)` stores the attempt in an `ISocialPostLedger` before sending, keyed by the
post's `IdempotencyKey`. The host backs it with its own table:
- `BeginAsync` is an atomic insert that returns the existing row on conflict (a `Failed` row is replaced).
- `SaveAsync` is a conditional update: only the same attempt (same token) still `Pending` changes, so one caller's late
  refusal never overwrites the post another caller landed on that attempt. It answers whether it wrote.

- A key that is `Posted` answers with the earlier post (`AlreadyPosted`) and sends nothing.
- A key that is `Pending` (the answer was lost) resumes its attempt. What happens depends on the network:

| Network | Mechanism | A retry after a lost answer |
| --- | --- | --- |
| Bluesky | The record key (a TID) is chosen before sending | reads the record back (`getRecord`), then sends under the same key only if it isn't there, and reads again if the create fails. Any time later. |
| Mastodon | `Idempotency-Key`, kept by the instance "for up to 1 hour" | resends with the same key within 50 minutes; later, `Unconfirmed`. Safe against a resend that overlaps the first request only on Mastodon 4.6 and later (the key became a lock there); a server that merely speaks the Mastodon API may not honour the key at all. |
| Discord bot | `nonce` + `enforce_nonce`, checked "in the past few minutes" | resends with the same nonce within 2 minutes; later, `Unconfirmed` |
| Discord webhook | none | `Unconfirmed` |
| X | none (X refuses an exact repeat of a recent post with 403, which arrives as `Rejected`) | `Unconfirmed` |

`Unconfirmed` means a person checks the network. If nothing is there, `PostAsync(channel, post, resendUnconfirmed: true)`
sends again. A refusal that created nothing (a rate limit, a credential, a validation error, a picture that didn't
upload, a connection that never opened) marks the attempt `Failed`, so the next call starts afresh.

## Per network

**Bluesky** (AT Protocol over HTTP, an app password).
- Session from `com.atproto.server.createSession`, kept in memory per account (createSession is rate-limited per account,
  more tightly than posting) and made again on `ExpiredToken`. `Service` defaults to `https://bsky.social`, which serves
  every Bluesky-hosted account; a self-hosted PDS sets its own.
- 300 graphemes and 3000 UTF-8 bytes. The link is shown short in the text (no scheme, at most 30 characters, as the
  Bluesky app writes it) under a link facet with the full URL; every URL and hashtag in the text gets its facet, indexed
  in UTF-8 bytes. A URL keeps a closing parenthesis it opened; tags keep combining marks (Hindi, Tamil); an all-digit
  word gets no tag.
- Without pictures, the card is an `app.bsky.embed.external` with the page's title and description; its thumb is
  uploaded as a blob, at most 1,000,000 bytes. A larger or unreachable thumb leaves the card without one, with a note.
- The post's address is `https://bsky.app/profile/<did>/post/<rkey>`: the DID, so it keeps working when the handle
  changes. `PostId` is the `at://` URI. An email identifier appears in `Id` and logs only as a hash.

**Mastodon** (an instance and an access token with `write:statuses` and `write:media`).
- `POST /api/v1/statuses` as a form, with `visibility` (`public` by default), `language` and `media_ids[]`.
- The limit is the instance's: `MaxCharacters` (default 500) and `CharactersReservedPerUrl` (default 23), both read from
  the instance's `GET /api/v2/instance` → `configuration.statuses`. Counted in graphemes, each URL at the reserved length.
- Without pictures, the instance builds the link card itself from the page.
- `MediaPollInterval` (1 s) and `MediaPollAttempts` (30) bound the wait for a picture still processing.
- The instance address must be public https.

**Discord** (an incoming webhook, or a bot in one channel).
- One message: the text (2000 characters), the pictures or else an embed with the card (title cut to 256, description
  to 4096, the link as the embed's URL), and `allowed_mentions: { parse: [] }`, so no text ever pings `@everyone`.
  Hashtags are left out. Without a card or pictures the link goes in the text and Discord unfurls it.
- Webhook: `POST /webhooks/<id>/<token>?wait=true`. The URL is the credential; only `discord.com` webhook URLs are
  taken, and every request goes to `https://discord.com/api/v10`.
- Bot: `Authorization: Bot <token>`, a nonce, and `Crosspost` to publish in an announcement channel to the servers that
  follow it. The bot needs View Channel, Send Messages, Embed Links and Attach Files (and Manage Messages to crosspost
  others' posts).
- The message address needs the server id, read once from the webhook or the channel, on its own short deadline once
  the message is up.

**X** (API v2, user context).
- **Needs a paid X API plan and a user-context credential.** A first-class channel like the others, but X's API is
  pay-per-use credits bought upfront, with no free posting tier
  ([pricing](https://docs.x.com/x-api/getting-started/pricing), read 2026-11-22: "Post: Create" $0.015, "Post: Create
  (with URL)" $0.200 per request), so a post with a link costs the URL price; check the same page for what media
  uploads cost. An app-only bearer token can't post.
- **Two ways to sign in, both user context:**
  - OAuth 1.0a, `new XAccount(apiKey, apiKeySecret, accessToken, accessTokenSecret)`: the developer app's keys and the
    app owner's own access token pair from the developer portal. They don't expire; the app needs Read and write
    permission before its access token is generated. Every request is signed (HMAC-SHA1, RFC 5849; a JSON or multipart
    body is not part of the signature, the STATUS query is). This is what a product posting to its own account uses.
  - OAuth 2.0, `new XAccount(accessToken)`: a PKCE user token with `tweet.read tweet.write users.read media.write`. It
    lasts two hours; `SocialChannels.RefreshXTokenAsync(client, refreshToken)` trades a refresh token
    (`offline.access`) for a new one. Store the refresh token the answer carries before using the access token: the
    one sent may be spent. An `Auth` failure there means the owner connects X again.
- 280 weighted characters, counted to stay at or over X's own count (twitter-text v3: most Latin text weighs 1, CJK
  and emoji 2; a ZWJ join, a text-presentation selector or a keycap on a letter per part; every http(s) URL 23, a
  bare domain or other dotted word at least 23; after NFC). Checked against twitter-text 3.1.0 on the cases in the
  tests, not against X itself, whose emoji table can be newer. `MaxWeightedLength` raises the limit for an X Premium
  account.
- Without pictures, X builds the card from the page. A picture's alt text that X refuses costs the description, not the
  picture: the post goes up with a note.
- `Id` is `x` and Mastodon's is the instance host: they don't tell two accounts on the same network apart, so keep the
  host's channel id in the idempotency key (`KeyFor`).

## Credentials and egress

- **The package stores nothing.** Each account type (`BlueskyAccount`, `MastodonAccount`, `DiscordWebhook`,
  `DiscordBotChannel`, `XAccount`, `XClient`) holds its secret in memory and prints without it. Keep them sealed at rest
  with `AgentKit.Secrets` (one sealed value per channel, associated data naming the channel row) and open them just
  before making the channel.
- **HTTP.** Two named clients: `AgentKit.Social` for the networks' APIs (public addresses on port 443, no redirects)
  and `AgentKit.Social.Media` for a card picture by address (public addresses on 80 and 443, up to three redirects).
  Both go through `AgentKit.Fetch`'s connect-time guard, since a Mastodon instance, a PDS and a picture URL are
  addresses someone typed. `SocialChannels` is made only by `AddSocialChannels`, on these clients.
- **Resource bounds, not usage limits.** The package sets no caps of its own on how much or how often anyone posts:
  what it refuses is what the network refuses (characters, picture count, size and format), with the network's
  reason. What it does bound is the machine's resources, sized to the networks' own caps:
  - each call has a deadline on the host's `TimeProvider`: `SocialDefaults.CallTimeout` (30 s) for an answer, and
    `UploadTimeout(bytes)` for an upload (30 s plus the bytes at 128 KiB/s, so X's 15 MB GIF gets about two and a half
    minutes and a 25 MiB Discord message about four);
  - the follow-up after a post is up (a crosspost, a lookup, a cleanup) has its own `AfterPostTimeout` (10 s) and never
    turns a post that is up into a failure;
  - an API answer over `MaxAnswerBytes` (1 MiB) is a transport failure, never buffered;
  - waiting for an upload to process is `MediaPollInterval` × `MediaPollAttempts`; a negative value is refused when the
    channel is made.
- **A transport failure on an upload** (a reset, a timeout) is a failure that posted nothing (`MayHavePosted` false),
  handled like a refused picture: Mastodon deletes what was already uploaded, and a retry starts afresh. On X, an alt
  text whose call breaks costs the description only, as a refused one does.
- **Logs.** Neither client writes HttpClient's request logs: a Discord webhook's path is its token. One Information line
  per post (`Posted to <channel id>: <post id>`) and one Warning per failure, with the category and the scrubbed message.
- **Traces.** HttpClient's own trace spans (`System.Net.Http`, read by OpenTelemetry's HttpClient instrumentation or
  Application Insights) carry `url.full` with the path; .NET redacts only the query. A host that exports those spans
  exports a Discord webhook's token with them. Either filter the span for `discord.com/api/v10/webhooks/` (the
  instrumentation's filter or enrich hook) or post through a bot channel, whose token travels in a header.

## Pictures are sent as given

The package uploads the bytes the host passes. Fitting a picture to a network (re-encoding, resizing under its byte
limit, keeping the first frame of an animation, stripping EXIF such as camera and GPS data) is the host's step before
it builds the post: the package takes no image library.

## Where it comes from

The adapters carry over Echo of Dreams' announcement cross-posters (Bluesky, X, Discord), generalized:

- **Kept:** Bluesky's createSession → uploadBlob → createRecord flow and lexicon shapes; X's OAuth 1.0a signer with its
  pinned worked-example vectors, its never-under weighted count, its 1-to-19-digit media id check and its "a refused
  alt text costs the description, not the picture"; Discord's `?wait=true` webhook embed with colour, footer and time
  (`LinkCard.Accent`, `Footer`, `PublishedAt`).
- **Changed, and why:**
  - Pictures post as native media whenever the host gives them, and a picture that fails stops the post. The
    cross-posters attached X's picture only when the text carried another link (their spec's S2b) and dropped a failed
    picture to post without it; the owner asked for direct picture posts with nothing missing (2026-11-22). Both
    older rules stay possible in the host: which `Images` to pass is its choice, and posting again without them after a
    failure is one more call.
  - A full http(s) URL counts exactly 23 on X and a dotted word the larger of its weight and 23; the cross-poster
    charged both its own weight plus 23, which never undercounts but refuses posts X takes.
  - Bluesky's session is kept per account in memory (createSession is rate-limited); the cross-poster signed in per
    post. Link and tag facets are added; `langs` is sent only when the post names a language.
  - Picture preparation stays with the host (above).
- **Stays with the host:** reading credentials at call time (a missing variable is "not configured"), the process-wide
  Discord 429 registry, a probe host's egress switch, and an X base URL for a local stub (tests here replace the
  HttpClient handler instead).

A test replaces the primary handlers (`AddHttpClient(SocialDefaults.ApiHttpClientName).ConfigurePrimaryHttpMessageHandler(...)`);
this package's tests pin every request shape against fakes of the four networks.
