using System.Net;
using System.Net.Http.Json;
using RetryMesh;
using Microsoft.Extensions.DependencyInjection;

namespace RetryMesh.IntegrationTests;

public class ChainTests
{
    [Theory]
    [InlineData(false, 9)]
    [InlineData(true, 3)]
    public async Task RealSampleChainHasExactCounts(bool enabled, int expected)
    {
        await using var c = ServiceCHost.Build(["--urls=http://127.0.0.1:0"]);
        await c.StartAsync();
        await using var b = ServiceBHost.Build(["--urls=http://127.0.0.1:0",
            $"--Downstream:BaseUrl={c.Urls.Single()}", $"--RetryMesh:Enabled={enabled}"]);
        await b.StartAsync();
        await using var a = ServiceAHost.Build(["--urls=http://127.0.0.1:0",
            $"--Downstream:BaseUrl={b.Urls.Single()}", $"--RetryMesh:Enabled={enabled}"]);
        await a.StartAsync();
        using var client = new HttpClient();
        using var reset = await client.PostAsync($"{c.Urls.Single()}/stats/reset", null);
        reset.EnsureSuccessStatusCode();
        using var response = await client.GetAsync($"{a.Urls.Single()}/execute");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(expected, c.Services.GetRequiredService<RequestCounter>().Count);
        var stats = await client.GetFromJsonAsync<Stats>($"{c.Urls.Single()}/stats");
        Assert.Equal(expected, stats!.RequestCount);
        var failure = RetryMeshHeaders.Read(response);
        if (enabled)
        {
            Assert.NotNull(failure);
            Assert.Equal("ServiceB", failure.RetriedBy);
            Assert.Equal(3, failure.Attempts);
        }
        else Assert.Null(failure);
    }

    [Fact]
    public async Task UnrelatedApplicationFailureDoesNotPropagateDownstreamMetadata()
    {
        await using var c = ServiceCHost.Build(["--urls=http://127.0.0.1:0"]);
        await c.StartAsync();
        await using var b = ServiceBHost.Build(["--urls=http://127.0.0.1:0",
            $"--Downstream:BaseUrl={c.Urls.Single()}", "--RetryMesh:Enabled=true"]);
        await b.StartAsync();
        using var client = new HttpClient();
        using var response = await client.GetAsync($"{b.Urls.Single()}/execute?unrelated=true");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(3, c.Services.GetRequiredService<RequestCounter>().Count);
        Assert.Null(RetryMeshHeaders.Read(response));
    }
    private sealed record Stats(int RequestCount);
}
