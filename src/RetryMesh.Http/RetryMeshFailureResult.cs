using Microsoft.AspNetCore.Http;

namespace RetryMesh;

/// <summary>Explicitly forwards the selected downstream failure, including validated exhaustion metadata.</summary>
public sealed class RetryMeshFailureResult : IResult
{
    private readonly int _statusCode;
    private readonly RetryMeshFailure? _failure;

    public RetryMeshFailureResult(HttpResponseMessage downstreamResponse)
    {
        if (downstreamResponse.IsSuccessStatusCode)
            throw new ArgumentException("Expected a failed downstream response.", nameof(downstreamResponse));
        _statusCode = (int)downstreamResponse.StatusCode;
        _failure = RetryMeshHeaders.Read(downstreamResponse);
    }

    public Task ExecuteAsync(HttpContext context)
    {
        context.Response.StatusCode = _statusCode;
        if (_failure is not null)
        {
            using var metadata = new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
            RetryMeshHeaders.Write(metadata, _failure);
            foreach (var header in metadata.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        return Task.CompletedTask;
    }
}
