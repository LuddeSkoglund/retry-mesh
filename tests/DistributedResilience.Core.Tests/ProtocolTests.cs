using System.Net;
using DistributedResilience;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DistributedResilience.Core.Tests;

public class ProtocolTests
{
    private static DistributedRetryFailure Failure => new()
    { FailureId = "abc-123", RetriedBy = "ServiceB", Attempts = 3, Outcome = DistributedRetryOutcome.Exhausted };
    [Fact]
    public void ValidHeadersRoundTrip()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        DistributedRetryHeaders.Write(response, Failure);
        Assert.Equal(Failure, DistributedRetryHeaders.Read(response));
    }
    [Fact]
    public void MissingHeadersAreIgnored()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.Null(DistributedRetryHeaders.Read(response));
    }
    [Theory]
    [InlineData("", "ServiceB", "abc", "exhausted")]
    [InlineData("-1", "ServiceB", "abc", "exhausted")]
    [InlineData("1", "ServiceB", "abc", "exhausted")]
    [InlineData("2147483648", "ServiceB", "abc", "exhausted")]
    [InlineData("three", "ServiceB", "abc", "exhausted")]
    [InlineData("3", "bad service", "abc", "exhausted")]
    [InlineData("3", "ServiceB", "", "exhausted")]
    [InlineData("3", "ServiceB", "abc", "unknown")]
    public void MalformedMetadataIsIgnored(string attempts, string by, string id, string status)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        response.Headers.TryAddWithoutValidation(DistributedRetryHeaders.Attempts, attempts);
        response.Headers.TryAddWithoutValidation(DistributedRetryHeaders.RetriedBy, by);
        response.Headers.TryAddWithoutValidation(DistributedRetryHeaders.FailureId, id);
        response.Headers.TryAddWithoutValidation(DistributedRetryHeaders.Status, status);
        Assert.Null(DistributedRetryHeaders.Read(response));
    }
    [Fact]
    public void DuplicateMetadataIsIgnored()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        DistributedRetryHeaders.Write(response, Failure);
        response.Headers.Add(DistributedRetryHeaders.Attempts, "3");
        Assert.Null(DistributedRetryHeaders.Read(response));
    }
    [Fact]
    public void SuccessCannotClaimFailure()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        DistributedRetryHeaders.Write(response, Failure);
        Assert.Null(DistributedRetryHeaders.Read(response));
    }
    [Fact]
    public async Task OnlyExplicitFailureResultPropagatesMetadata()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        DistributedRetryHeaders.Write(response, Failure);
        var forwarded = new DefaultHttpContext();
        await new DistributedFailureResult(response).ExecuteAsync(forwarded);
        Assert.Equal("abc-123", forwarded.Response.Headers[DistributedRetryHeaders.FailureId]);
        var unrelated = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        await Results.StatusCode(500).ExecuteAsync(unrelated);
        Assert.False(unrelated.Response.Headers.ContainsKey(DistributedRetryHeaders.Status));
    }
}
