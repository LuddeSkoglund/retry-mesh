using System.Net;
using RetryMesh;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace RetryMesh.Http.Tests;

public class ProtocolTests
{
    private static RetryMeshFailure Failure => new()
    { FailureId = "abc-123", RetriedBy = "ServiceB", Attempts = 3, Outcome = RetryMeshOutcome.Exhausted };
    [Fact]
    public void ValidHeadersRoundTrip()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        RetryMeshHeaders.Write(response, Failure);
        Assert.Equal(Failure, RetryMeshHeaders.Read(response));
    }
    [Fact]
    public void MissingHeadersAreIgnored()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        Assert.Null(RetryMeshHeaders.Read(response));
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
        response.Headers.TryAddWithoutValidation(RetryMeshHeaders.Attempts, attempts);
        response.Headers.TryAddWithoutValidation(RetryMeshHeaders.RetriedBy, by);
        response.Headers.TryAddWithoutValidation(RetryMeshHeaders.FailureId, id);
        response.Headers.TryAddWithoutValidation(RetryMeshHeaders.Status, status);
        Assert.Null(RetryMeshHeaders.Read(response));
    }
    [Fact]
    public void DuplicateMetadataIsIgnored()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        RetryMeshHeaders.Write(response, Failure);
        response.Headers.Add(RetryMeshHeaders.Attempts, "3");
        Assert.Null(RetryMeshHeaders.Read(response));
    }
    [Fact]
    public void SuccessCannotClaimFailure()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        RetryMeshHeaders.Write(response, Failure);
        Assert.Null(RetryMeshHeaders.Read(response));
    }
    [Fact]
    public async Task ExplicitFailureResultForwardsOnlySelectedMetadataWithoutMiddleware()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        RetryMeshHeaders.Write(response, Failure);
        response.Headers.Add("X-Unrelated", "private");
        var result = new RetryMeshFailureResult(response);
        response.Dispose();
        var forwarded = new DefaultHttpContext();
        await result.ExecuteAsync(forwarded);
        Assert.Equal("abc-123", forwarded.Response.Headers[RetryMeshHeaders.FailureId]);
        Assert.False(forwarded.Response.Headers.ContainsKey("X-Unrelated"));
        var unrelated = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        await Results.StatusCode(500).ExecuteAsync(unrelated);
        Assert.False(unrelated.Response.Headers.ContainsKey(RetryMeshHeaders.Status));
    }
}
