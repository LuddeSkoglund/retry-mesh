using System.Net;
using DistributedResilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace DistributedResilience.Core.Tests;

public class RetryIntegrationTests
{
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    public async Task MetadataControlsOnlyAdditionalRetries(bool metadata, int expected)
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (metadata) DistributedRetryHeaders.Write(response, new()
            { FailureId = "original", RetriedBy = "Downstream", Attempts = 3, Outcome = DistributedRetryOutcome.Exhausted });
            return response;
        });
        using var provider = Configure(handler);
        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://test/fail");
        Assert.Equal(expected, handler.Count);
        var failure = DistributedRetryHeaders.Read(response);
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
        Assert.Null(DistributedRetryHeaders.Read(response));
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
        Assert.NotNull(DistributedRetryHeaders.Read(response));
    }

    private static ServiceProvider Configure(StubHandler handler, Action<HttpStandardResilienceOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddDistributedResilience();
        services.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.Delay = TimeSpan.Zero;
                options.Retry.UseJitter = false;
                configure?.Invoke(options);
            }).UseDistributedRetries("Caller");
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
