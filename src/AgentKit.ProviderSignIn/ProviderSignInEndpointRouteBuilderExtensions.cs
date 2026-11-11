using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentKit.ProviderSignIn;

/// <summary>Maps the sign-in endpoints.</summary>
public static class ProviderSignInEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps, under <see cref="ProviderSignInOptions.PathPrefix"/> (default <c>/api/auth</c>):
    /// <list type="bullet">
    /// <item><c>GET providers</c>: <c>{"providers":["google","discord"]}</c>, the enabled ones only, for the sign-in page.</item>
    /// <item><c>GET {provider}/start?returnTo=…</c>: begins a plain sign-in (<see cref="ProviderSignInPurposes.SignIn"/>) and redirects to the provider.</item>
    /// <item><c>GET {provider}/callback</c>: the redirect URI; completes the sign-in and answers with whatever
    /// <see cref="IProviderSignInCompletion"/> returns.</item>
    /// </list>
    /// A provider that is not enabled gets 404 on both. Every answer is <c>no-store</c>, and the callback's is
    /// <c>Referrer-Policy: no-referrer</c> (its URL carries the code). Returns the group, so the host can add rate limiting.
    /// </summary>
    public static RouteGroupBuilder MapProviderSignIn(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<ProviderSignInOptions>();
        var group = endpoints.MapGroup(options.PathPrefix);
        group.AddEndpointFilter(async (ctx, next) =>
        {
            ctx.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(ctx);
        });

        group.MapGet("/providers", (ProviderSignInFlow flow) => Results.Json(new { providers = flow.EnabledProviders }));

        group.MapGet("/{provider}/start", async (string provider, string? returnTo, HttpContext context, ProviderSignInFlow flow) =>
        {
            if (!flow.IsEnabled(provider)) return Results.NotFound();
            var url = await flow.BeginAsync(provider, context, new ProviderSignInContext(ProviderSignInPurposes.SignIn, null, returnTo), context.RequestAborted);
            return Results.Redirect(url.AbsoluteUri);
        });

        group.MapGet("/{provider}/callback", async (string provider, HttpContext context, ProviderSignInFlow flow) =>
        {
            if (!flow.IsEnabled(provider)) return Results.NotFound();
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var completion = context.RequestServices.GetRequiredService<IProviderSignInCompletion>();
            var (success, failure) = await flow.CompleteAsync(provider, context, context.RequestAborted);
            return success is not null
                ? await completion.SignedInAsync(success, context, context.RequestAborted)
                : await completion.FailedAsync(failure!, context, context.RequestAborted);
        });

        return group;
    }
}
