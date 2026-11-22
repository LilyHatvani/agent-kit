# AgentKit.Channels

Outgoing messages to the places people read, without a vendor SDK. Written for Agent Blog's notifications (M2 slice
3); shaped so Relay and EoD can send through it too.

| Destination | Builder | Address check |
| --- | --- | --- |
| Discord channel webhook | `Discord.Request(webhook, message, username)`: one embed, `allowed_mentions: { parse: [] }`, the body's markdown escaped, `?wait=true` | `Discord.WebhookRefusal` |
| Slack incoming webhook | `Slack.Request(webhook, message)`: a section block, `& < >` escaped, no unfurl | `Slack.WebhookRefusal` |
| ntfy | `Ntfy.Request(server, topic, token, message)`: JSON publish to the server root, token as bearer | `Ntfy.ServerRefusal`, `Ntfy.TopicRefusal` |
| Signed webhook | `StandardWebhooks.Request(url, secret, messageId, now, message)`: `webhook-id`, `-timestamp`, `-signature` (v1 HMAC-SHA256) | `StandardWebhooks.UrlRefusal` |

- **One message shape**, `ChannelMessage(Type, Title, Body, Link, Data)`. Title and body may be agent-written: every
  builder escapes them for its destination and cuts them to its limits (`Text.Truncate`, never mid surrogate pair).
- **One reading of the answer**, `SendOutcome.ReadAsync(response, now, ct)`: `Sent` (2xx), `Retry` (429 with its
  `Retry-After`, capped at 12 hours; 408, 425, 5xx), `Gone` (401, 403, 404, 410: stop and tell the owner), `Failed`
  (another 4xx). The error carries the status and the start of the body, decoded leniently.
- **One retry schedule**, `Retries.Next(attemptsMade, retryAfter)`: 1 m, 5 m, 30 m, 2 h, 12 h, then dead.
- **Receivers' side** for tests: `StandardWebhooks.Verify` (tolerance 5 minutes, constant-time compare, several
  signatures).

Send through a guarded client (`AgentKit.Fetch`): every one of these addresses is one someone typed in. Web Push is
`AgentKit.Push`. Posting to public social accounts (Bluesky, Mastodon) is a separate package, built on `SendOutcome` and
`Retries` from here.
