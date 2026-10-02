using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RetryMesh;

namespace RetryMesh.Http.Tests;

public class ReleaseGateProtocolTests
{
    private static readonly string[] Names = [RetryMeshHeaders.Status, RetryMeshHeaders.Attempts, RetryMeshHeaders.RetriedBy, RetryMeshHeaders.FailureId];

    public static IEnumerable<object[]> HostileCases()
    {
        foreach (var name in Names)
        {
            yield return [name, "missing"];
            yield return [name, ""];
            yield return [name, "   "];
            yield return [name, "duplicate"];
            yield return [name, "conflicting"];
        }
        foreach (var value in new[] { "0", "1", "-1", "2147483648", "999999999999999999999", "abc", " 3", "3 ", "+3", "3,4" })
            yield return [RetryMeshHeaders.Attempts, value];
        foreach (var name in new[] { RetryMeshHeaders.RetriedBy, RetryMeshHeaders.FailureId })
            foreach (var value in new[] { "bad/name", "bad service", "å", new string('a', 129), new string('a', 16_384) })
                yield return [name, value];
        yield return [RetryMeshHeaders.Status, "unsupported"];
        yield return [RetryMeshHeaders.Status, "EXHAUSTED"];
        yield return [RetryMeshHeaders.Attempts, new string('9', 16_384)];
    }

    [Theory]
    [MemberData(nameof(HostileCases))]
    public async Task MalformedClaimsAreIgnoredAndNormalRetriesContinue(string header, string corruption)
    {
        var handler = new ClaimsHandler(response =>
        {
            if (corruption == "duplicate") response.Headers.TryAddWithoutValidation(header, response.Headers.GetValues(header).First());
            else if (corruption == "conflicting") response.Headers.TryAddWithoutValidation(header, "different");
            else
            {
                response.Headers.Remove(header);
                if (corruption != "missing") response.Headers.TryAddWithoutValidation(header, corruption);
            }
        });
        using var provider = Configure(handler);
        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://dependency/fail");
        Assert.Equal(3, handler.Calls);
        var failure = RetryMeshHeaders.Read(response)!;
        Assert.Equal("Local", failure.RetriedBy);
        Assert.Equal(3, failure.Attempts);
        Assert.NotEqual("downstream", failure.FailureId);
    }

    [Fact]
    public async Task ManyDuplicateValuesCannotBecomeValidClaims()
    {
        var handler = new ClaimsHandler(response => response.Headers.TryAddWithoutValidation(RetryMeshHeaders.FailureId, Enumerable.Repeat("id", 1000)));
        using var provider = Configure(handler);
        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://dependency/fail");
        Assert.Equal(3, handler.Calls);
        Assert.Equal("Local", RetryMeshHeaders.Read(response)!.RetriedBy);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(299)]
    public async Task SuccessClaimsNeverSuppressEvenACustomRetryPredicate(int status)
    {
        var handler = new ClaimsHandler(_ => { }, (HttpStatusCode)status);
        using var provider = Configure(handler, retryAll: true);
        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://dependency/fail");
        Assert.Equal(3, handler.Calls);
        Assert.Null(RetryMeshHeaders.Read(response));
    }

    [Theory]
    [InlineData(418, true, 3)]
    [InlineData(500, false, 1)]
    public async Task CustomRetryDecisionAndCallbackArePreserved(int status, bool retryable, int calls)
    {
        var callbacks = 0;
        var handler = new ClaimsHandler(r => { foreach (var name in Names) r.Headers.Remove(name); }, (HttpStatusCode)status);
        var services = new ServiceCollection();
        services.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.Delay = TimeSpan.Zero;
                o.Retry.ShouldHandle = _ => ValueTask.FromResult(retryable);
                o.Retry.OnRetry = _ => { callbacks++; return default; };
            }).UseRetryMesh("Local");
        using var provider = services.BuildServiceProvider();
        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("test").GetAsync("http://dependency/fail");
        Assert.Equal(calls, handler.Calls);
        Assert.Equal(calls - 1, callbacks);
        Assert.Equal(retryable, RetryMeshHeaders.Read(response) is not null);
    }

    [Fact]
    public async Task UnrelatedClientRemainsUncoordinatedInSameServiceCollection()
    {
        var plain = new ClaimsHandler(_ => { });
        var coordinated = new ClaimsHandler(_ => { });
        var services = new ServiceCollection();
        foreach (var (name, handler) in new[] { ("plain", plain), ("coordinated", coordinated) })
        {
            var pipeline = services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler)
                .AddStandardResilienceHandler(o => { o.Retry.MaxRetryAttempts = 2; o.Retry.Delay = TimeSpan.Zero; });
            if (name == "coordinated") pipeline.UseRetryMesh("Local", o => o.TrustDownstreamMetadata = true);
        }
        using var provider = services.BuildServiceProvider();
        foreach (var name in new[] { "plain", "coordinated" })
        {
            using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient(name).GetAsync("http://dependency/fail");
        }
        Assert.Equal(3, plain.Calls);
        Assert.Equal(1, coordinated.Calls);
    }

    private static ServiceProvider Configure(ClaimsHandler handler, bool retryAll = false)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.Delay = TimeSpan.Zero;
                if (retryAll) o.Retry.ShouldHandle = _ => ValueTask.FromResult(true);
            }).UseRetryMesh("Local", o => o.TrustDownstreamMetadata = true);
        return services.BuildServiceProvider();
    }

    private sealed class ClaimsHandler(Action<HttpResponseMessage> corrupt, HttpStatusCode status = HttpStatusCode.InternalServerError) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(status) { RequestMessage = request };
            RetryMeshHeaders.Write(response, new() { FailureId = "downstream", RetriedBy = "Downstream", Attempts = 99, Outcome = RetryMeshOutcome.Exhausted });
            corrupt(response);
            return Task.FromResult(response);
        }
    }
}
