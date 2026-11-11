using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace AgentKit.McpOAuth.Internal;

/// <summary>OpenIddict's EF Core stores. The schema comes from <see cref="McpOAuthSchema"/>, never EF migrations.</summary>
internal sealed class McpOAuthDbContext(DbContextOptions<McpOAuthDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseOpenIddict();
    }
}

/// <summary>Revokes an OpenIddict authorization and every token issued under it.</summary>
internal sealed class McpOAuthGrants(IServiceScopeFactory scopes) : IMcpOAuthGrants
{
    public async Task RevokeAsync(string authorizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(authorizationId);
        await using var scope = scopes.CreateAsyncScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        if (await authorizations.FindByIdAsync(authorizationId, cancellationToken) is { } authorization)
            await authorizations.TryRevokeAsync(authorization, cancellationToken);
        await tokens.RevokeByAuthorizationIdAsync(authorizationId, cancellationToken);
    }
}

/// <summary>
/// The signing and encryption keys, read from <c>oauth_key</c> and created there on first use; and the consent page's
/// HMAC key, derived from the signing key so every replica shares it without another row. Loaded once, when OpenIddict
/// first resolves its options (after the host's migrations ran). Two replicas racing to create a key both read the row
/// that won. The kid is the first 16 characters of the base64url SHA-256 of the public key.
/// </summary>
internal sealed class McpOAuthKeys(McpOAuthOptions options, TimeProvider clock) : IConfigureOptions<OpenIddictServerOptions>
{
    private readonly Lazy<(RsaSecurityKey Signing, RsaSecurityKey Encryption, byte[] Consent)> _keys = new(() => Load(options, clock));

    public byte[] ConsentKey => _keys.Value.Consent;

    public void Configure(OpenIddictServerOptions options)
    {
        var (signing, encryption, _) = _keys.Value;
        options.SigningCredentials.Add(new SigningCredentials(signing, SecurityAlgorithms.RsaSha256));
        options.EncryptionCredentials.Add(new EncryptingCredentials(encryption, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
    }

    private static (RsaSecurityKey, RsaSecurityKey, byte[]) Load(McpOAuthOptions options, TimeProvider clock)
    {
        using var conn = new NpgsqlConnection(options.ConnectionString);
        conn.Open();
        var signingPem = Pem(conn, "signing", clock);
        var encryptionPem = Pem(conn, "encryption", clock);
        var consent = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(signingPem), 32,
            info: Encoding.UTF8.GetBytes("AgentKit.McpOAuth consent unlock v1"));
        return (Key(signingPem), Key(encryptionPem), consent);
    }

    private static string Pem(NpgsqlConnection conn, string name, TimeProvider clock)
    {
        if (Read(conn, name) is { } pem) return pem;
        using (var fresh = RSA.Create(2048))
        using (var insert = new NpgsqlCommand("INSERT INTO oauth_key (name, pem, created_at) VALUES (@name, @pem, @now) ON CONFLICT (name) DO NOTHING", conn))
        {
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("pem", fresh.ExportPkcs8PrivateKeyPem());
            insert.Parameters.AddWithValue("now", clock.GetUtcNow());
            insert.ExecuteNonQuery();
        }
        return Read(conn, name) ?? throw new InvalidOperationException($"The oauth_key row '{name}' could not be created.");
    }

    private static string? Read(NpgsqlConnection conn, string name)
    {
        using var select = new NpgsqlCommand("SELECT pem FROM oauth_key WHERE name = @name", conn);
        select.Parameters.AddWithValue("name", name);
        return select.ExecuteScalar() as string;
    }

    private static RsaSecurityKey Key(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var kid = Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportRSAPublicKey()))[..16];
        return new RsaSecurityKey(rsa) { KeyId = kid };
    }
}
