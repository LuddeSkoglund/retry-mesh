using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetryMesh;

namespace RetryMesh.IntegrationTests;

public class ReleaseGateOrderingTests
{
    [Theory]
    [InlineData("before-routing", true, true)]
    [InlineData("before-auth", true, true)]
    [InlineData("after-auth", true, true)]
    [InlineData("after-endpoints", true, false)]
    [InlineData("before-routing", false, false)]
    [InlineData("before-auth", false, false)]
    [InlineData("after-auth", false, false)]
    [InlineData("after-endpoints", false, false)]
    public async Task RoutingAndAuthenticationOrderingIsObserved(string placement, bool authenticated, bool propagated)
    {
        var calls = 0;
        await using var leaf = ReleaseGateTransportTests.NewHost().Build();
        leaf.MapGet("/", () => { Interlocked.Increment(ref calls); return Results.StatusCode(500); });
        await leaf.StartAsync();
        var builder = ReleaseGateTransportTests.NewHost();
        builder.Services.AddAuthentication("Gate").AddScheme<AuthenticationSchemeOptions, GateAuthentication>("Gate", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpClient("dependency", c => c.BaseAddress = new Uri(leaf.Urls.Single()))
            .AddStandardResilienceHandler(o => { o.Retry.MaxRetryAttempts = 2; o.Retry.Delay = TimeSpan.Zero; })
            .UseRetryMesh("Proxy");
        await using var proxy = builder.Build();
        proxy.UseExceptionHandler(handler => handler.Run(c => { c.Response.StatusCode = 500; return Task.CompletedTask; }));
        if (placement == "before-routing") proxy.UseRetryMesh();
        proxy.UseRouting();
        if (placement == "before-auth") proxy.UseRetryMesh();
        proxy.UseAuthentication();
        proxy.UseAuthorization();
        if (placement == "after-auth") proxy.UseRetryMesh();
#pragma warning disable ASP0014 // Explicit endpoint dispatch is required to test unreachable middleware.
        proxy.UseEndpoints(endpoints => endpoints.MapGet("/", async (IHttpClientFactory clients) =>
        {
            using var response = await clients.CreateClient("dependency").GetAsync("/");
            return Results.StatusCode((int)response.StatusCode);
        }).RequireAuthorization());
#pragma warning restore ASP0014
        if (placement == "after-endpoints") proxy.UseRetryMesh();
        await proxy.StartAsync();
        using var caller = new HttpClient();
        using var result = await caller.GetAsync(proxy.Urls.Single() + (authenticated ? "/?authenticated=true" : "/"));
        Assert.Equal(authenticated ? 500 : 401, (int)result.StatusCode);
        Assert.Equal(authenticated ? 3 : 0, calls);
        Assert.Equal(propagated, RetryMeshHeaders.Read(result) is not null);
    }

    private sealed class GateAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logs, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logs, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Query.ContainsKey("authenticated")
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "gate-user")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}
