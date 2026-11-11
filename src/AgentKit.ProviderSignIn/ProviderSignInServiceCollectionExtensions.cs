using AgentKit.ProviderSignIn.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentKit.ProviderSignIn;

/// <summary>Registers the provider sign-in.</summary>
public static class ProviderSignInServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ProviderSignInFlow"/>, its provider <see cref="HttpClient"/>
    /// (<see cref="ProviderSignInDefaults.HttpClientName"/>) and a state store: PostgreSQL when
    /// <see cref="ProviderSignInOptions.ConnectionString"/> is set, in memory otherwise, unless the host registered its own
    /// <see cref="IProviderSignInStateStore"/> first. Each provider is enabled only when its client id and secret are set;
    /// with none, the flow offers nothing and the endpoints answer 404.
    /// </summary>
    /// <remarks>
    /// The host also registers an <see cref="IProviderSignInCompletion"/> (who the identity is, and the session) and maps
    /// <see cref="ProviderSignInEndpointRouteBuilderExtensions.MapProviderSignIn"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The options are inconsistent (a provider without <see cref="ProviderSignInOptions.PublicUrl"/>, half a client…).</exception>
    public static IServiceCollection AddProviderSignIn(this IServiceCollection services, Action<ProviderSignInOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ProviderSignInOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(ProviderSignInDefaults.HttpClientName, c => c.Timeout = options.ProviderTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            services.TryAddSingleton<IProviderSignInStateStore, InMemoryStateStore>();
        else
            services.TryAddSingleton<IProviderSignInStateStore>(new PostgresStateStore(options.ConnectionString));
        services.AddSingleton(sp => new GoogleIdTokens(options, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(sp => new ProviderSignInFlow(options, sp.GetRequiredService<IProviderSignInStateStore>(),
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<GoogleIdTokens>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>()));
        return services;
    }
}
