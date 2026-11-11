# AgentKit.ProviderSignIn

Server-driven Google and Discord sign-in for ASP.NET Core. Your server is the relying party: it sends the browser to
the provider, takes the code back, exchanges it itself and hands your app a verified **(provider, account id)**.

The package proves which provider account signed in. Your app decides who that is.

## What it does

- **Authorization code with PKCE S256.**
  - Each sign-in gets a random state, a PKCE verifier and a nonce, stored server-side.
  - The provider's code is exchanged server-side with the client secret and the verifier. The browser never sees a token.
- **One registered redirect per provider.** It is `<PublicUrl><PathPrefix>/{provider}/callback`, built from
  configuration, never from the request's Host.
- **The state is single-use, short-lived and bound to the browser.**
  - The callback deletes the state it reads, so a replay finds nothing.
  - It is good for 10 minutes.
  - It must match a `__Host-` cookie set when the sign-in began. Another browser's code and state (login CSRF) are
    refused, and that refusal does not spend the owner's sign-in.
  - A state begun for one provider is refused at the other's callback.
  - Only the state's SHA-256 is stored.
- **Google: the ID token is checked.**
  - RS256 signature against Google's published keys. They are cached for an hour, fetched again once for an unknown key
    id, and at most once a minute.
  - Issuer, audience (your client id) and expiry.
  - The nonce of this very sign-in, so a code injected from another authorization request is refused.
  - The account id is the token's `sub`.
- **Discord: the user id is read from `/users/@me`** with the access token, and must be a snowflake.
  - PKCE parameters are sent to Discord too. Its OAuth2 page recommends PKCE but does not document `code_challenge` or
    `code_verifier`, so for Discord the protection rests on the client secret, the state and the cookie. A provider
    that ignores PKCE ignores the parameters; one that checks them gets the right ones.
- **No provider tokens are kept.** The access token is used once (Discord's user read) and dropped. Nothing is
  refreshed or stored.
- **Each provider is optional.** It is enabled only when its client id and secret are both set. `GET providers` lists
  the enabled ones for your sign-in page. A provider that is not enabled answers 404 at `start` and `callback`.
- **Coarse failures.** A refused sign-in reaches your app as one of `state_invalid`, `state_expired`, `denied`,
  `exchange_failed`, `identity_rejected` or `provider_unavailable`. The details go to the log, never to the browser.

## What it does not do

- **It never decides who the user is.** It hands over the identity. Binding it, checking it against your owner or
  users, and starting a session are yours.
- **It never matches by email.** `Email` and `DisplayName` are display hints. Emails change and get recycled, and a
  matching address is not the same person. Bind and look up by `(Provider, AccountId)` only.
- **No sessions, no cookies of your own, no users table.** The one cookie it sets is the state binding, deleted at the
  callback.

## Use it

```csharp
builder.Services.AddProviderSignIn(o =>
{
    o.PublicUrl = builder.Configuration["PublicUrl"];        // https://app.example.com
    o.Google.ClientId = builder.Configuration["Auth:Google:ClientId"];
    o.Google.ClientSecret = builder.Configuration["Auth:Google:ClientSecret"];
    o.Discord.ClientId = builder.Configuration["Auth:Discord:ClientId"];
    o.Discord.ClientSecret = builder.Configuration["Auth:Discord:ClientSecret"];
    o.ConnectionString = connectionString;                   // optional: PostgreSQL for sign-ins in flight
});
builder.Services.AddScoped<IProviderSignInCompletion, MySignInCompletion>();

app.MapProviderSignIn();                                     // /api/auth/providers, /{provider}/start, /{provider}/callback
```

Register with each provider:

| Provider | Console | Redirect URI |
| --- | --- | --- |
| Google | Google Cloud console → APIs & Services → Credentials → OAuth client ID (Web application) | `https://app.example.com/api/auth/google/callback` |
| Discord | Discord Developer Portal → your application → OAuth2 → Redirects | `https://app.example.com/api/auth/discord/callback` |

## What the host implements

### `IProviderSignInCompletion`

- `SignedInAsync(success, context, ct)` receives `success.Identity` (`Provider`, `AccountId`, `DisplayName`, `Email`,
  `EmailVerified`) and `success.Context` (`Purpose`, `Data`, `ReturnTo`). Return the response:
  - Look up the bound `(Provider, AccountId)`.
  - Then set your session and redirect, or refuse.
- `FailedAsync(failure, context, ct)` receives `failure.Code` and, once the state was read, `failure.Context`. Return
  the response, typically a redirect to your sign-in page with `?error=<code>`.

```csharp
public sealed class MySignInCompletion(Owners owners, Sessions sessions) : IProviderSignInCompletion
{
    public async ValueTask<IResult> SignedInAsync(ProviderSignInSuccess s, HttpContext http, CancellationToken ct)
    {
        var id = s.Identity;
        switch (s.Context.Purpose)
        {
            case "setup":                                    // begun by your own endpoint, after it checked a setup token
                await owners.BindFirstAsync(id.Provider, id.AccountId, id.DisplayName, ct);
                break;
            case "bind":                                     // begun by a signed-in owner; Data names that session
                if (!await sessions.IsLiveAsync(s.Context.Data, ct)) return Results.Redirect("/signin?error=session");
                await owners.BindAnotherAsync(id.Provider, id.AccountId, id.DisplayName, ct);
                break;
            default:                                         // ProviderSignInPurposes.SignIn
                if (!await owners.IsBoundAsync(id.Provider, id.AccountId, ct)) return Results.Redirect("/signin?error=not_owner");
                break;
        }
        await sessions.StartAsync(http, ct);
        return Results.Redirect(s.Context.ReturnTo ?? "/");
    }

    public ValueTask<IResult> FailedAsync(ProviderSignInFailure f, HttpContext http, CancellationToken ct) =>
        ValueTask.FromResult(Results.Redirect("/signin?error=" + f.Code));
}
```

### Purposes: beginning a sign-in yourself

`GET {provider}/start` begins a plain sign-in (`ProviderSignInPurposes.SignIn`). An anonymous browser cannot ask for
anything else.

For a setup or a bind, map your own endpoint, check what it needs (a setup token, a session), then begin the sign-in
with your context:

```csharp
app.MapPost("/api/auth/setup", async (SetupForm form, HttpContext http, ProviderSignInFlow flow) =>
{
    if (!SetupToken.Matches(form.Token)) return Results.Unauthorized();
    var url = await flow.BeginAsync(form.Provider, http, new ProviderSignInContext("setup", ReturnTo: "/"));
    return Results.Redirect(url.AbsoluteUri);
});
```

The context stays server-side with the state and comes back at the callback, so the browser cannot change it.

`ReturnTo` is kept only as a path on your site:

- A relative path such as `/settings` is kept.
- An absolute URL on `PublicUrl`'s origin is kept as its path and query.
- Anything else (another origin, `//host`, a backslash) becomes null.

### Optional: `IProviderSignInStateStore`

- Sign-ins in flight live in memory by default. That suits one process; a restart only loses the sign-ins in flight.
- Set `ConnectionString` to keep them in PostgreSQL across restarts and replicas.
- Or register your own store before `AddProviderSignIn`. `TakeAsync` must be atomic.

## Schema

`ProviderSignInSchema.ReadScript()` returns the PostgreSQL script: one table, `provider_signin_state`. Run it with your
own migrator; every statement is `IF NOT EXISTS`. The app's role needs SELECT, INSERT and DELETE on it. Rows expire
after 10 minutes and are swept on the next start.

## Options

| Option | Default | |
| --- | --- | --- |
| `PublicUrl` | none | the origin the browser sees; https, or http on loopback only. Required once a provider is configured |
| `PathPrefix` | `/api/auth` | where the endpoints live |
| `StateLifetime` | 10 minutes | |
| `ConnectionString` | none (in memory) | PostgreSQL for sign-ins in flight |
| `ProviderTimeout` | 10 seconds | per provider call |
| `Google.Scope` | `openid email profile` | |
| `Google.Issuers` | Google's two spellings | |
| `Discord.Scope` | `identify` | add `email` for the email hint |
| `Discord.Prompt` | none sent | `none` sends an account that already authorized the app straight back; Discord does not document it for a first authorization |
| `*.AuthorizeEndpoint`, `*.TokenEndpoint`, `Google.JwksUri`, `Discord.UserEndpoint` | the providers' own | for tests and proxies |

Every provider call goes through the named `HttpClient` `ProviderSignInDefaults.HttpClientName`. Tests replace its
primary handler.

## Operational notes

- **Rate-limit the endpoints.** `MapProviderSignIn()` returns the route group, so you can add `.RequireRateLimiting(…)`.
  Each `start` writes a state row.
- **Responses.** Every response is `Cache-Control: no-store`. The callback adds `Referrer-Policy: no-referrer`, because
  its URL carries the code.
- **Logging.** Refused states log at Information: junk, stale tabs and replays are anonymous and free to send. ID token
  and exchange failures log at Warning, with the exception type only. The client secret and codes are never logged.
