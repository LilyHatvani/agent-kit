# AgentKit.Channels

Outgoing messages to the places people read, without a vendor SDK. Written for Agent Blog's notifications (M2 slice
3); shaped so Relay and EoD can send through it too.

| Destination | Builder | Address check |
| --- | --- | --- |
| Discord channel webhook | `Discord.Request(webhook, message, username)`: one embed, `allowed_mentions: { parse: [] }`, the body's markdown escaped, the name cut to 80, `?wait=true` | `Discord.WebhookRefusal`, `DiscordWebhookAddress` |
| Slack incoming webhook | `Slack.Request(webhook, message)`: a section block, `& < >` escaped, no unfurl | `Slack.WebhookRefusal` (no query or fragment) |
| ntfy | `Ntfy.Request(server, topic, token, message)`: JSON publish to the server root, token as bearer | `Ntfy.ServerRefusal`, `Ntfy.TopicRefusal` |
| Signed webhook | `StandardWebhooks.Request(url, secret, messageId, now, message)`: `webhook-id`, `-timestamp`, `-signature` (v1 HMAC-SHA256) | `StandardWebhooks.UrlRefusal` |

- **One message shape**, `ChannelMessage(Type, Title, Body, Link, Data)`. Title and body may be agent-written: every
  builder escapes them for its destination and cuts them to its limits (`Text.Truncate`, which cuts between the
  characters people see: never inside a surrogate pair, a flag, a ZWJ emoji or a letter and its accent).
- **One reading of a Discord webhook URL**, `DiscordWebhookAddress.TryParse` / `Parse`: https on `discord.com`,
  `discordapp.com`, `canary.` or `ptb.discord.com`, the path `/api[/v<n>]/webhooks/<id>/<token>`, an id of ASCII digits
  that fits 64 bits. It gives the id, the token (the credential), and the canonical `Uri` and `ExecuteUri`
  (`https://discord.com/api/v10/webhooks/<id>/<token>?wait=true`); it prints without the token and no refusal repeats
  the URL. `Discord.WebhookRefusal` is that reading. `Discord.NoMentions()` is the `allowed_mentions` object, fresh
  for each payload, and `Discord.Max*` are Discord's limits.
- **One reading of the answer**, `SendOutcome.ReadAsync(response, now, ct)`: `Sent` (2xx), `Retry` (429 with its
  `Retry-After`, capped at 12 hours, or 1 minute without one; 408, 425, 5xx), `Gone` (401, 403, 404, 410: stop and tell
  the owner), `Failed` (another status). The error carries the status and the start of the body, decoded leniently. Its
  two parts are public: `SendOutcome.Classify(status)` reads a status without the body, and
  `SendOutcome.RetryAfterOf(response, now, max)` reads `Retry-After` (whole seconds or a date, seconds with a fraction
  too) and nothing else: a sign, an exponent, `NaN` or words is no header, never a throw and never a wait that beats a
  caller's better source. `SendOutcome.Bounded(wait, max)` keeps a wait between none and a cap.
- **One retry schedule**, `Retries.Next(attemptsMade, retryAfter)`: 1 m, 5 m, 30 m, 2 h, 12 h, then dead.
- **Receivers' side**: `StandardWebhooks.Verify` (tolerance 5 minutes, constant-time compare, several
  signatures), `IsFresh` (the timestamp check alone) and `SecretRefusal` (a secret checked at startup). AgentKit.Mail
  verifies Resend's delivery webhooks with them.

Send through a guarded client (`AgentKit.Fetch`): every one of these addresses is one someone typed in. **A Discord or
Slack webhook's path is its token**, so register that client with `.RemoveAllLoggers()`: HttpClient's default request
logs write the URI, path and all. HttpClient's trace spans (`System.Net.Http`, exported by OpenTelemetry or Application
Insights) carry `url.full` with the path too, and .NET redacts only the query: filter those spans for the webhook hosts
before exporting them. Web Push is `AgentKit.Push`.

**AgentKit.Social** (public posts to Bluesky, Mastodon, Discord, X, Slack and Telegram) is built on this package: its
Discord channel reads the webhook URL with `DiscordWebhookAddress` and posts with `NoMentions()` and the
`Discord.Max*` limits; its Slack channel checks the URL with `Slack.WebhookRefusal` and escapes with `Slack.Escape`;
it cuts with `Text.Truncate` and reads every network's refusal with `SendOutcome.Classify`, `RetryAfterOf` and
`Bounded`. It keeps what a public post needs and a notification doesn't: its ledger instead of `Retries`, and on
Discord the post's text as written instead of `Discord.Escape` (it shows the same words on every network; nothing in it
pings anyone).
