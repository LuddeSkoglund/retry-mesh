using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetryMesh;

namespace RetryMesh.Http.Tests;

public class ServiceNameTests
{
    [Fact]
    public async Task HostApplicationNameIsSharedAcrossClientsAndWrittenToMetadata()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = "Checkout.Service" });
        var first = new FailureHandler();
        var second = new FailureHandler();
        AddClient(builder.Services, "orders", first).UseRetryMesh();
        AddClient(builder.Services, "inventory", second).UseRetryMesh();
        using var host = builder.Build();
        foreach (var name in new[] { "orders", "inventory" })
        {
            using var response = await host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name).GetAsync("http://dependency/fail");
            Assert.Equal("Checkout.Service", RetryMeshHeaders.Read(response)!.RetriedBy);
        }
        Assert.Equal(3, first.Calls);
        Assert.Equal(3, second.Calls);
    }

    [Fact]
    public async Task OutsideHostUsesEntryAssemblySimpleName()
    {
        var services = new ServiceCollection();
        var handler = new FailureHandler();
        AddClient(services, "test", handler).UseRetryMesh();
        using var provider = services.BuildServiceProvider();
        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://dependency/fail");
        Assert.Equal(Assembly.GetEntryAssembly()!.GetName().Name, RetryMeshHeaders.Read(response)!.RetriedBy);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("Hosted.Service")]
    [InlineData("invalid host name")]
    public async Task ExplicitNameOverridesHostName(string applicationName)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = applicationName });
        AddClient(builder.Services, "test", new FailureHandler()).UseRetryMesh("Chosen.Service");
        using var host = builder.Build();
        using var response = await host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://dependency/fail");
        Assert.Equal("Chosen.Service", RetryMeshHeaders.Read(response)!.RetriedBy);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task OptionsOnlyOverloadRetainsTrustConfiguration(bool trust, int expectedCalls)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = "Checkout" });
        var handler = new FailureHandler { SendClaims = true };
        AddClient(builder.Services, "test", handler).UseRetryMesh(o => o.TrustDownstreamMetadata = trust);
        using var host = builder.Build();
        using var response = await host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://dependency/fail");
        Assert.Equal(expectedCalls, handler.Calls);
        Assert.Equal(trust ? "Downstream" : "Checkout", RetryMeshHeaders.Read(response)!.RetriedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid name")]
    [InlineData("tjänst")]
    public async Task InvalidInferredNameFailsClearlyBeforeSending(string applicationName)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.ApplicationName = applicationName;
        var handler = new FailureHandler();
        AddClient(builder.Services, "test", handler).UseRetryMesh();
        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var response = await host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://dependency/fail");
        });
        Assert.Contains("explicit name", exception.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid name")]
    public void ExplicitNameStillValidatesAtRegistration(string serviceName)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => AddClient(services, "test", new FailureHandler()).UseRetryMesh(serviceName));
    }

    private static Microsoft.Extensions.Http.Resilience.IHttpStandardResiliencePipelineBuilder AddClient(
        IServiceCollection services, string name, FailureHandler handler) =>
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.Delay = TimeSpan.Zero;
                o.Retry.UseJitter = false;
                o.CircuitBreaker.MinimumThroughput = 100;
            });

    private sealed class FailureHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool SendClaims { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request };
            if (SendClaims)
                RetryMeshHeaders.Write(response, new() { FailureId = "original", RetriedBy = "Downstream", Attempts = 3, Outcome = RetryMeshOutcome.Exhausted });
            return Task.FromResult(response);
        }
    }
}
