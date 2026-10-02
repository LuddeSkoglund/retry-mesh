using System.Net;
using RetryMesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace RetryMesh.Http.Tests;

public class RetryIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MultipleClientsPreserveExplicitPolicyRegardlessOfRegistrationOrder(bool configureFirst)
    {
        var services = new ServiceCollection();
        if (configureFirst) services.AddRetryMesh(o => o.PropagationMode = RetryMeshPropagationMode.Explicit);
        services.AddHttpClient("first").AddStandardResilienceHandler().UseRetryMesh("Caller");
        services.AddHttpClient("second").AddStandardResilienceHandler().UseRetryMesh("Caller");
        if (!configureFirst) services.AddRetryMesh(o => o.PropagationMode = RetryMeshPropagationMode.Explicit);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(RetryMeshPropagationMode.Explicit, provider.GetRequiredService<IOptions<RetryMeshOptions>>().Value.PropagationMode);
        Assert.Single(provider.GetServices<IHttpContextAccessor>());
        Assert.Single(provider.GetServices<IValidateOptions<RetryMeshOptions>>());
    }
    [Theory]
    [InlineData(false, false, 3)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 3)]
    public async Task DownstreamClaimsRequireTrustAndValidProtocol(bool trusted, bool malformed, int attempts)
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            RetryMeshHeaders.Write(response, new() { FailureId = "fake", RetriedBy = "FakeExternal", Attempts = 99, Outcome = RetryMeshOutcome.Exhausted });
            if (malformed) response.Headers.Add(RetryMeshHeaders.Attempts, "3");
            return response;
        });
        using var provider = Configure(handler, trust: trusted);
        using var result = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://test/fail");
        Assert.Equal(attempts, handler.Count);
        var failure = RetryMeshHeaders.Read(result)!;
        Assert.Equal(trusted && !malformed ? "FakeExternal" : "Caller", failure.RetriedBy);
        Assert.Equal(trusted && !malformed ? 99 : 3, failure.Attempts);
    }

    [Fact]
    public async Task DefaultTrustIsFalseAndStandardStrategiesRetainConfiguration()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            RetryMeshHeaders.Write(response, new() { FailureId = "forged", RetriedBy = "External", Attempts = 99, Outcome = RetryMeshOutcome.Exhausted });
            return response;
        });
        var services = new ServiceCollection();
        services.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.Delay = TimeSpan.Zero;
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(7);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(25);
                o.CircuitBreaker.MinimumThroughput = 71;
            }).UseRetryMesh("Caller").Configure(o =>
            {
                Assert.Equal(TimeSpan.FromSeconds(7), o.AttemptTimeout.Timeout);
                Assert.Equal(TimeSpan.FromSeconds(25), o.TotalRequestTimeout.Timeout);
                Assert.Equal(71, o.CircuitBreaker.MinimumThroughput);
            });
        using var provider = services.BuildServiceProvider();
        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://test/fail");
        Assert.Equal(3, handler.Count);
        Assert.Equal("Caller", RetryMeshHeaders.Read(response)!.RetriedBy);
    }
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    public async Task MetadataControlsOnlyAdditionalRetries(bool metadata, int expected)
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (metadata) RetryMeshHeaders.Write(response, new()
            { FailureId = "original", RetriedBy = "Downstream", Attempts = 3, Outcome = RetryMeshOutcome.Exhausted });
            return response;
        });
        using var provider = Configure(handler);
        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://test/fail");
        Assert.Equal(expected, handler.Count);
        var failure = RetryMeshHeaders.Read(response);
        Assert.NotNull(failure);
        Assert.Equal(metadata ? "Downstream" : "Caller", failure.RetriedBy);
        if (metadata) Assert.Equal("original", failure.FailureId);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    public async Task NonRetryableFinalResponseIsNotMarked(int finalStatus)
    {
        var handler = new StubHandler(n => new HttpResponseMessage(n < 3 ? HttpStatusCode.InternalServerError : (HttpStatusCode)finalStatus));
        using var provider = Configure(handler);
        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://test/fail");
        Assert.Equal(3, handler.Count);
        Assert.Null(RetryMeshHeaders.Read(response));
    }

    [Fact]
    public async Task ExistingPredicateAndCallbackArePreserved()
    {
        var callbacks = 0;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var provider = Configure(handler, options =>
        {
            options.Retry.ShouldHandle = _ => ValueTask.FromResult(true);
            options.Retry.OnRetry = _ => { callbacks++; return default; };
        });
        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://test/fail");
        Assert.Equal(3, handler.Count);
        Assert.Equal(2, callbacks);
        Assert.NotNull(RetryMeshHeaders.Read(response));
    }

    private static ServiceProvider Configure(StubHandler handler, Action<HttpStandardResilienceOptions>? configure = null, bool trust = true)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.Delay = TimeSpan.Zero;
                options.Retry.UseJitter = false;
                configure?.Invoke(options);
            }).UseRetryMesh("Caller", options => options.TrustDownstreamMetadata = trust);
        return services.BuildServiceProvider();
    }

    private sealed class StubHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var result = response(Interlocked.Increment(ref _count));
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }
}
