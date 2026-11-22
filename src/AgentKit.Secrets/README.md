# AgentKit.Secrets

Secrets at rest for a self-hosted service: webhook URLs, API tokens, signing secrets, private keys. Lifted from Relay's
credential store (its decision 0024) and used by Relay and Agent Blog.

- **A keyring from configuration.** `<Section>:MasterKey` (env `<Section>__MasterKey`) is 32 random bytes, base64 or
  64 hex digits (`openssl rand -hex 32`, Railway's `secret(64, hex)`). It seals every new value. `<Section>:PreviousKeys`,
  comma-separated, only opens what an older key sealed, until the host re-seals those values under the current key.
- **Keys have ids that open nothing.** `k` + 16 hex digits of SHA-256 over the host's label and the key. A row stores
  the id of the key that sealed it.
- **No master key is a state, not a crash.** `On` is false, the feature that needs secrets says so, and everything else
  runs. A malformed key throws at start.
- **AES-256-GCM, a fresh nonce, associated data naming the row.** A ciphertext copied to another row, or altered, does
  not open. `Sealer.AssociatedData(version, parts…)` joins a version and the row's names one per line and refuses a part
  with a line break.

```csharp
var keys = MasterKeys.From(builder.Configuration, new MasterKeyOptions("Blog:Secrets", "agentblog-master-key-id:"));
builder.Services.AddSingleton(new Sealer(keys));

var ad = Sealer.AssociatedData("agentblog-secret:v1", $"channel:{channelId}:url");
SealedValue sealedUrl = sealer.Seal(url, ad);           // store Ciphertext, Nonce, KeyId on the row
string? url = sealer.OpenText(sealedUrl, ad);            // null when it doesn't open; sealer.Why(...) says why
```

The host owns its table, its label (never change it once a value is sealed) and its rekey: select the rows whose key
id isn't `keys.CurrentId`, open, seal again, update.
