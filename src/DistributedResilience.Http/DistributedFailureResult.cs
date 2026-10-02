using Microsoft.AspNetCore.Http;

namespace DistributedResilience;

/// <summary>Explicitly forwards the selected downstream failure, including validated exhaustion metadata.</summary>
public sealed class DistributedFailureResult : IResult
{
    private readonly int _statusCode;
    private readonly DistributedRetryFailure? _failure;

    public DistributedFailureResult(HttpResponseMessage downstreamResponse)
    {
        if (downstreamResponse.IsSuccessStatusCode)
            throw new ArgumentException("Expected a failed downstream response.", nameof(downstreamResponse));
        _statusCode = (int)downstreamResponse.StatusCode;
        _failure = DistributedRetryHeaders.Read(downstreamResponse);
    }

    public Task ExecuteAsync(HttpContext context)
    {
        context.Response.StatusCode = _statusCode;
        if (_failure is not null)
        {
            using var metadata = new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
            DistributedRetryHeaders.Write(metadata, _failure);
            foreach (var header in metadata.Headers)
                context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        return Task.CompletedTask;
    }
}
