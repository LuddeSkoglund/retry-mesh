using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetryMesh;

namespace RetryMesh.IntegrationTests;

public class ReleaseGateOutcomeTests
{
    [Theory]
    [InlineData(500, 503)]
    [InlineData(503, 500)]
    [InlineData(429, 500)]
    [InlineData(500, 200)]
    public async Task DiscardedDownstreamOutcomeCannotMarkADifferentFinalStatus(int downstreamStatus, int finalStatus)
    {
        var calls = 0;
        await using var leaf = ReleaseGateTransportTests.NewHost().Build();
        leaf.MapGet("/", () => { Interlocked.Increment(ref calls); return Results.StatusCode(downstreamStatus); });
        await leaf.StartAsync();
        var builder = ReleaseGateTransportTests.NewHost();
        builder.Services.AddHttpClient("dependency", c => c.BaseAddress = new Uri(leaf.Urls.Single()))
            .AddStandardResilienceHandler(o => { o.Retry.MaxRetryAttempts = 2; o.Retry.Delay = TimeSpan.Zero; })
            .UseRetryMesh("Proxy");
        await using var proxy = builder.Build();
        proxy.UseRetryMesh();
        proxy.MapGet("/", async (IHttpClientFactory clients) =>
        {
            using var response = await clients.CreateClient("dependency").GetAsync("/");
            return Results.StatusCode(finalStatus);
        });
        await proxy.StartAsync();
        using var caller = new HttpClient();
        using var result = await caller.GetAsync(proxy.Urls.Single());
        Assert.Equal(3, calls);
        Assert.Equal(finalStatus, (int)result.StatusCode);
        Assert.Null(RetryMeshHeaders.Read(result));
    }

    [Theory]
    [InlineData(500, 503, 503, true, true)]
    [InlineData(500, 500, 500, true, false)]
    [InlineData(500, 503, 503, false, true)]
    [InlineData(500, 500, 500, false, false)]
    [InlineData(500, 200, 200, false, false)]
    [InlineData(500, 200, 500, false, false)]
    [InlineData(500, 503, 500, false, true)]
    [InlineData(503, 503, 500, false, false)]
    [InlineData(500, 500, 503, false, false)]
    [InlineData(429, 429, 500, false, false)]
    public async Task SequentialAndParallelBranchesSelectOnlyUniqueMatchingFailures(
        int firstStatus, int secondStatus, int finalStatus, bool parallel, bool propagated)
    {
        var counts = new ConcurrentDictionary<string, int>();
        await using var leaf = ReleaseGateTransportTests.NewHost().Build();
        leaf.MapGet("/{branch}/{status:int}", (string branch, int status) =>
        {
            counts.AddOrUpdate(branch, 1, (_, n) => n + 1);
            return Results.StatusCode(status);
        });
        await leaf.StartAsync();
        var builder = ReleaseGateTransportTests.NewHost();
        var logs = new ReleaseGateLoggingTests.CaptureLogs();
        builder.Logging.AddProvider(logs).AddFilter("RetryMesh", Microsoft.Extensions.Logging.LogLevel.Debug);
        builder.Services.AddHttpClient("dependency", c => c.BaseAddress = new Uri(leaf.Urls.Single()))
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.Delay = TimeSpan.Zero;
                o.CircuitBreaker.MinimumThroughput = 100;
            }).UseRetryMesh("ServiceB");
        await using var proxy = builder.Build();
        proxy.UseRetryMesh();
        RetryMeshFailure? firstFailure = null;
        RetryMeshFailure? secondFailure = null;
        proxy.MapGet("/", async (IHttpClientFactory clients) =>
        {
            var client = clients.CreateClient("dependency");
            if (parallel)
            {
                var responses = await Task.WhenAll(client.GetAsync($"/C/{firstStatus}"), client.GetAsync($"/D/{secondStatus}"));
                using var first = responses[0];
                using var second = responses[1];
                firstFailure = RetryMeshHeaders.Read(first);
                secondFailure = RetryMeshHeaders.Read(second);
            }
            else
            {
                using var first = await client.GetAsync($"/C/{firstStatus}");
                firstFailure = RetryMeshHeaders.Read(first);
                using var second = await client.GetAsync($"/D/{secondStatus}");
                secondFailure = RetryMeshHeaders.Read(second);
            }
            return Results.StatusCode(finalStatus);
        });
        await proxy.StartAsync();
        using var caller = new HttpClient();
        using var response = await caller.GetAsync(proxy.Urls.Single());
        Assert.Equal(finalStatus, (int)response.StatusCode);
        Assert.Equal(3, counts["C"]);
        Assert.Equal(secondStatus == 200 ? 1 : 3, counts["D"]);
        var failure = RetryMeshHeaders.Read(response);
        Assert.Equal(propagated, failure is not null);
        if (propagated)
            Assert.Equal(finalStatus == firstStatus ? firstFailure!.FailureId : secondFailure!.FailureId, failure!.FailureId);
        else
            Assert.Contains(logs.Events, e => e.Message.Contains("propagation skipped") && e.Fields.ContainsKey("Reason"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ParallelSuccessAndFailureUseDocumentedConservativeCompletionRule(bool successFirst, bool propagated)
    {
        var counts = new ConcurrentDictionary<int, int>();
        await using var leaf = ReleaseGateTransportTests.NewHost().Build();
        leaf.MapGet("/{status:int}", (int status) =>
        {
            counts.AddOrUpdate(status, 1, (_, n) => n + 1);
            return Results.StatusCode(status);
        });
        await leaf.StartAsync();
        var builder = ReleaseGateTransportTests.NewHost();
        builder.Services.AddHttpClient("dependency", c => c.BaseAddress = new Uri(leaf.Urls.Single()))
            .AddStandardResilienceHandler(o => { o.Retry.MaxRetryAttempts = 2; o.Retry.Delay = TimeSpan.Zero; })
            .UseRetryMesh("ServiceB");
        await using var proxy = builder.Build();
        proxy.UseRetryMesh();
        proxy.MapGet("/", async (IHttpClientFactory clients) =>
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Call(int status)
            {
                if ((status == 200) != successFirst) await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var result = await clients.CreateClient("dependency").GetAsync($"/{status}");
                if ((status == 200) == successFirst) completed.SetResult();
            }
            await Task.WhenAll(Call(200), Call(500));
            return Results.StatusCode(500);
        });
        await proxy.StartAsync();
        using var caller = new HttpClient();
        using var response = await caller.GetAsync(proxy.Urls.Single());
        Assert.Equal(1, counts[200]);
        Assert.Equal(3, counts[500]);
        Assert.Equal(propagated, RetryMeshHeaders.Read(response) is not null);
    }
}
