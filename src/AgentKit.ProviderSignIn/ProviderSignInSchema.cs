namespace AgentKit.ProviderSignIn;

/// <summary>
/// The package's PostgreSQL schema: one table, <c>provider_signin_state</c>, for sign-ins in flight. Needed only with
/// <see cref="ProviderSignInOptions.ConnectionString"/>. The package never migrates; the host runs
/// <see cref="ReadScript"/> once with its own migrator. Every statement is <c>IF NOT EXISTS</c>. The role the app
/// connects as needs SELECT, INSERT and DELETE on the table.
/// </summary>
public static class ProviderSignInSchema
{
    /// <summary>The embedded resource's name in the package assembly.</summary>
    public const string ResourceName = "AgentKit.ProviderSignIn.Schema.provider-signin.sql";

    /// <summary>The tables the schema creates, for a host that grants per table.</summary>
    public static IReadOnlyList<string> Tables { get; } = ["provider_signin_state"];

    /// <summary>The schema script (PostgreSQL), as shipped in this package version.</summary>
    public static string ReadScript()
    {
        using var stream = typeof(ProviderSignInSchema).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
