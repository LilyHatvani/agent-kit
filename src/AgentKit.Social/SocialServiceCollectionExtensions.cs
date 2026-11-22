using AgentKit.Fetch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentKit.Social;

/// <summary>Registers the social channels.</summary>
public static class SocialServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SocialChannels"/> (the factory the host makes channels with, from its own credentials), a
    /// <see cref="SocialPublisher"/>, and an in-memory <see cref="ISocialPostLedger"/> unless the host registered its own
    /// first. Two HttpClients (<see cref="SocialDefaults.ApiHttpClientName"/>, <see cref="SocialDefaults.MediaHttpClientName"/>)
    /// connect only to public addresses and log nothing.
    /// </summary>
    public static IServiceCollection AddSocialChannels(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        // No request logging on either client: a Discord webhook's path is its token, and the default HttpClient logs
        // write the request URI.
        // No client-wide timeout: each call carries its own deadline (ChannelBase.SendAsync), sized to what it uploads.
        // The answer is capped: a hostile instance or PDS can't make the host buffer a huge body.
        services.AddHttpClient(SocialDefaults.ApiHttpClientName, c =>
            {
                c.Timeout = Timeout.InfiniteTimeSpan;
                c.MaxResponseContentBufferSize = SocialDefaults.MaxAnswerBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => GuardedHttp.CreateHandler(new GuardedHttpOptions { Ports = [443] }))
            // No handler from the host's ConfigureHttpClientDefaults (a standard resilience handler retries a 5xx): a
            // retried post on a network without an idempotency key is a second post. The ledger decides retries.
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear())
            .RemoveAllLoggers();
        services.AddHttpClient(SocialDefaults.MediaHttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => GuardedHttp.CreateHandler(new GuardedHttpOptions { Ports = [80, 443], FollowRedirects = true }))
            .RemoveAllLoggers();
        services.TryAddSingleton(sp => new SocialChannels(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()));
        services.TryAddSingleton<ISocialPostLedger, InMemorySocialPostLedger>();
        services.TryAddSingleton(sp => new SocialPublisher(sp.GetRequiredService<ISocialPostLedger>()));
        return services;
    }
}
