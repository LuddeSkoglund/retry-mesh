using RetryMesh;

namespace RetryMesh.IntegrationTests;

public class ReleaseGateChainTests
{
    [Theory]
    [InlineData(3, false, false, false, 9)]
    [InlineData(3, true, false, false, 3)]
    [InlineData(3, false, true, false, 9)]
    [InlineData(3, true, true, false, 3)]
    [InlineData(3, false, false, true, 9)]
    [InlineData(3, true, false, true, 3)]
    [InlineData(3, true, true, true, 3)]
    [InlineData(4, false, false, false, 27)]
    [InlineData(4, true, false, false, 3)]
    [InlineData(4, true, true, true, 3)]
    public async Task ExactChainCountsAndIdentity(int length, bool enabled, bool mvc, bool typed, int leafCalls)
    {
        await using var chain = await ReleaseGateChain.Start(length, enabled, mvc, typed);
        using var client = new HttpClient();
        using var response = await client.GetAsync(chain.Url + "/execute?root=one");
        Assert.Equal(500, (int)response.StatusCode);
        for (var i = 0; i < length; i++)
            Assert.Equal(enabled ? (i == length - 1 ? 3 : 1) : (int)Math.Pow(3, i), chain.Nodes[i].Incoming);
        Assert.Equal(leafCalls, chain.Nodes[^1].Incoming);
        var failure = RetryMeshHeaders.Read(response);
        if (!enabled) { Assert.Null(failure); return; }
        Assert.NotNull(failure);
        Assert.Equal(chain.Nodes[^2].Name, failure.RetriedBy);
        Assert.Equal(3, failure.Attempts);
        foreach (var node in chain.Nodes.Take(length - 1))
            Assert.Equal(failure.FailureId, node.FailureIds["one"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RecoveryNeverCreatesExhaustion(int recoverAt)
    {
        await using var chain = await ReleaseGateChain.Start(recoverAt: recoverAt);
        using var client = new HttpClient();
        using var response = await client.GetAsync(chain.Url + "/execute?root=recovery");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(new[] { 1, 1, recoverAt }, chain.Nodes.Select(n => n.Incoming));
        Assert.Equal(recoverAt - 1, chain.Nodes[1].RetryCallbacks);
        Assert.Null(RetryMeshHeaders.Read(response));
        Assert.All(chain.Nodes, n => Assert.Empty(n.FailureIds));
    }

    [Theory]
    [InlineData(500, 3, true)]
    [InlineData(502, 3, true)]
    [InlineData(503, 3, true)]
    [InlineData(429, 3, true)]
    [InlineData(400, 1, false)]
    [InlineData(404, 1, false)]
    public async Task StandardPredicateRetainsStatusSemantics(int status, int calls, bool exhausted)
    {
        await using var chain = await ReleaseGateChain.Start(terminalStatus: status);
        using var client = new HttpClient();
        using var response = await client.GetAsync(chain.Url + "/execute?root=status");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(new[] { 1, 1, calls }, chain.Nodes.Select(n => n.Incoming));
        Assert.Equal(exhausted, RetryMeshHeaders.Read(response) is not null);
    }

    [Theory]
    [InlineData(true, false, 3)]
    [InlineData(false, true, 9)]
    public async Task FakeExternalAndMalformedInternalClaimsFailSafely(bool fakeExternal, bool malformedInternal, int leafCalls)
    {
        await using var chain = await ReleaseGateChain.Start(fakeExternal: fakeExternal, malformedInternal: malformedInternal);
        using var client = new HttpClient();
        using var response = await client.GetAsync(chain.Url + "/execute?root=trust");
        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal(leafCalls, chain.Nodes[^1].Incoming);
        Assert.Equal(fakeExternal ? 1 : 3, chain.Nodes[1].Incoming);
        var failure = RetryMeshHeaders.Read(response)!;
        Assert.Equal(fakeExternal ? "ServiceB" : "ServiceA", failure.RetriedBy);
        Assert.Equal(3, failure.Attempts);
        Assert.NotEqual("fake", failure.FailureId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PartialAdoptionFallsBackToNormalAmplification(int missingNode)
    {
        await using var chain = await ReleaseGateChain.Start(unconfiguredNode: missingNode);
        using var client = new HttpClient();
        using var response = await client.GetAsync(chain.Url + "/execute?root=mixed");
        Assert.Equal(new[] { 1, 3, 9 }, chain.Nodes.Select(n => n.Incoming));
        if (missingNode == 0) Assert.Null(RetryMeshHeaders.Read(response));
        else Assert.Equal("ServiceA", RetryMeshHeaders.Read(response)!.RetriedBy);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(100, true)]
    [InlineData(1000, false)]
    public async Task RepeatedAndConcurrentRequestsHaveIndependentIdentities(int requests, bool concurrent)
    {
        await using var chain = await ReleaseGateChain.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        async Task<string> Execute(int index)
        {
            var root = index.ToString();
            using var response = await client.GetAsync(chain.Url + "/execute?root=" + root);
            Assert.Equal(500, (int)response.StatusCode);
            var failure = RetryMeshHeaders.Read(response)!;
            Assert.Equal("ServiceB", failure.RetriedBy);
            Assert.Equal(3, failure.Attempts);
            Assert.Equal(1, chain.Nodes[0].RootCounts[root]);
            Assert.Equal(1, chain.Nodes[1].RootCounts[root]);
            Assert.Equal(3, chain.Nodes[2].RootCounts[root]);
            Assert.Equal(failure.FailureId, chain.Nodes[1].FailureIds[root]);
            return failure.FailureId;
        }
        var ids = new List<string>();
        if (concurrent) ids.AddRange(await Task.WhenAll(Enumerable.Range(0, requests).Select(Execute)));
        else for (var i = 0; i < requests; i++) ids.Add(await Execute(i));
        Assert.Equal(requests, ids.Distinct().Count());
        Assert.Equal(new[] { requests, requests, requests * 3 }, chain.Nodes.Select(n => n.Incoming));
        Assert.Equal(0, chain.Nodes[0].RetryCallbacks);
        Assert.Equal(requests * 2, chain.Nodes[1].RetryCallbacks);
        foreach (var node in chain.Nodes.Take(2))
        {
            for (var i = 0; i < requests; i++) Assert.True(await node.Completed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(requests, node.CandidateSizes.Count);
            Assert.All(node.CandidateSizes, size => Assert.Equal(1, size));
        }
    }
}
