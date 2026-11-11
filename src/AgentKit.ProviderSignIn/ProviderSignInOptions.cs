namespace AgentKit.ProviderSignIn;

/// <summary>
/// What the host tells the sign-in at registration (<see cref="ProviderSignInServiceCollectionExtensions.AddProviderSignIn"/>).
/// Binds from configuration: <c>configuration.GetSection("Auth").Bind(o)</c> reads <c>PublicUrl</c>,
/// <c>Google:ClientId</c>, <c>Google:ClientSecret</c>, <c>Discord:ClientId</c> and so on.
/// </summary>
public sealed class ProviderSignInOptions
{
    /// <summary>
    /// The site's public origin as the browser sees it, such as <c>https://relay.example.com</c> (a path base is allowed):
    /// the base of every redirect URI. Always configuration, never the request's Host. https, except on a loopback host
    /// for local development. Required as soon as one provider is configured.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Where the endpoints live under <see cref="PublicUrl"/>. Default <c>/api/auth</c>: <c>/api/auth/providers</c>,
    /// <c>/api/auth/{provider}/start</c> and <c>/api/auth/{provider}/callback</c>, the redirect URI to register with each
    /// provider.
    /// </summary>
    public string PathPrefix { get; set; } = "/api/auth";

    /// <summary>Google. Enabled when both its client id and secret are set.</summary>
    public GoogleSignInOptions Google { get; } = new();

    /// <summary>Discord. Enabled when both its client id and secret are set.</summary>
    public DiscordSignInOptions Discord { get; } = new();

    /// <summary>How long a started sign-in may take to come back. Default 10 minutes.</summary>
    public TimeSpan StateLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// PostgreSQL for the sign-ins in flight (schema from <see cref="ProviderSignInSchema.ReadScript"/>): they then survive
    /// a restart and work across replicas. Null or blank keeps them in memory, which is right for one process. A host can
    /// also register its own <see cref="IProviderSignInStateStore"/>.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>How long one call to a provider (token exchange, user read, key fetch) may take. Default 10 seconds.</summary>
    public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal Uri? PublicUri { get; private set; }

    internal void Validate()
    {
        if (StateLifetime <= TimeSpan.Zero) throw new InvalidOperationException("ProviderSignInOptions.StateLifetime must be positive.");
        if (ProviderTimeout <= TimeSpan.Zero) throw new InvalidOperationException("ProviderSignInOptions.ProviderTimeout must be positive.");
        if (string.IsNullOrEmpty(PathPrefix) || PathPrefix[0] != '/' || PathPrefix.EndsWith('/') || PathPrefix.Contains("//", StringComparison.Ordinal)
            || PathPrefix.Contains('?', StringComparison.Ordinal) || PathPrefix.Contains('#', StringComparison.Ordinal))
            throw new InvalidOperationException("ProviderSignInOptions.PathPrefix must start with '/' and not end with one, such as /api/auth.");
        Google.Validate("Google");
        Discord.Validate("Discord");

        if (string.IsNullOrWhiteSpace(PublicUrl))
        {
            if (Google.IsConfigured || Discord.IsConfigured)
                throw new InvalidOperationException("ProviderSignInOptions.PublicUrl is required when a provider is configured: it is the base of the redirect URI.");
            return;
        }

        if (!Uri.TryCreate(PublicUrl.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException($"ProviderSignInOptions.PublicUrl '{PublicUrl}' is not an origin (with an optional path).");
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new InvalidOperationException("ProviderSignInOptions.PublicUrl must be https (http only on a loopback host).");
        PublicUri = uri;
    }
}

/// <summary>One provider's OAuth client. The endpoints default to the provider's own; tests and proxies may point them elsewhere.</summary>
public abstract class ProviderClientOptions
{
    /// <summary>The OAuth client id from the provider's developer console.</summary>
    public string? ClientId { get; set; }

    /// <summary>The OAuth client secret. Never logged, never sent to the browser.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>The scopes asked for, space-separated.</summary>
    public abstract string Scope { get; set; }

    /// <summary>The provider's authorization endpoint, where the browser is sent.</summary>
    public abstract string AuthorizeEndpoint { get; set; }

    /// <summary>The provider's token endpoint, where the server exchanges the code.</summary>
    public abstract string TokenEndpoint { get; set; }

    /// <summary>Whether both the client id and secret are set: the provider is then offered.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    internal virtual void Validate(string name)
    {
        if (!string.IsNullOrWhiteSpace(ClientId) != !string.IsNullOrWhiteSpace(ClientSecret))
            throw new InvalidOperationException($"ProviderSignInOptions.{name}: set both the client id and the client secret, or neither.");
        if (!IsConfigured) return;
        RequireUrl(name, nameof(AuthorizeEndpoint), AuthorizeEndpoint);
        RequireUrl(name, nameof(TokenEndpoint), TokenEndpoint);
        if (string.IsNullOrWhiteSpace(Scope)) throw new InvalidOperationException($"ProviderSignInOptions.{name}.Scope is required.");
    }

    internal static void RequireUrl(string name, string property, string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException($"ProviderSignInOptions.{name}.{property} '{value}' is not an absolute URL.");
    }
}

/// <summary>Google (OpenID Connect). The account id is the ID token's <c>sub</c>.</summary>
public sealed class GoogleSignInOptions : ProviderClientOptions
{
    /// <summary>Default <c>openid email profile</c>: the ID token, and the name and email as display hints.</summary>
    public override string Scope { get; set; } = "openid email profile";

    /// <summary>Default <c>https://accounts.google.com/o/oauth2/v2/auth</c>.</summary>
    public override string AuthorizeEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    /// <summary>Default <c>https://oauth2.googleapis.com/token</c>.</summary>
    public override string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";

    /// <summary>Google's signing keys. Default <c>https://www.googleapis.com/oauth2/v3/certs</c>.</summary>
    public string JwksUri { get; set; } = "https://www.googleapis.com/oauth2/v3/certs";

    /// <summary>
    /// The issuers an ID token may name. Empty means Google's two documented spellings, <c>https://accounts.google.com</c>
    /// and <c>accounts.google.com</c>.
    /// </summary>
    public IList<string> Issuers { get; } = [];

    internal IReadOnlyList<string> EffectiveIssuers => Issuers.Count > 0 ? [.. Issuers] : ["https://accounts.google.com", "accounts.google.com"];

    internal override void Validate(string name)
    {
        base.Validate(name);
        if (IsConfigured) RequireUrl(name, nameof(JwksUri), JwksUri);
    }
}

/// <summary>Discord (OAuth 2.0). The account id is the user's snowflake id from <c>/users/@me</c>.</summary>
public sealed class DiscordSignInOptions : ProviderClientOptions
{
    /// <summary>Default <c>identify</c>: the user id and names. Add <c>email</c> for the email display hint.</summary>
    public override string Scope { get; set; } = "identify";

    /// <summary>Default <c>https://discord.com/oauth2/authorize</c>.</summary>
    public override string AuthorizeEndpoint { get; set; } = "https://discord.com/oauth2/authorize";

    /// <summary>Default <c>https://discord.com/api/oauth2/token</c>.</summary>
    public override string TokenEndpoint { get; set; } = "https://discord.com/api/oauth2/token";

    /// <summary>The current user. Default <c>https://discord.com/api/users/@me</c>.</summary>
    public string UserEndpoint { get; set; } = "https://discord.com/api/users/@me";

    /// <summary>
    /// Discord's <c>prompt</c>. Default null: not sent, and Discord shows its authorization screen. <c>none</c> sends an
    /// account that already authorized the app straight back (a one-tap daily sign-in), but Discord does not document what
    /// it does for an account that has not authorized yet (a first setup), so try it before relying on it.
    /// </summary>
    public string? Prompt { get; set; }

    internal override void Validate(string name)
    {
        base.Validate(name);
        if (IsConfigured) RequireUrl(name, nameof(UserEndpoint), UserEndpoint);
    }
}
