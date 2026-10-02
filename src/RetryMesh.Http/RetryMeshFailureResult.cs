using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace RetryMesh;

/// <summary>Explicitly forwards the selected downstream failure, including validated exhaustion metadata.</summary>
public sealed class RetryMeshFailureResult : IResult, IActionResult
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
        if (context.Features.Get<RetryMeshRequestState>() is { } state) state.ExplicitResult = true;
        context.Response.StatusCode = _statusCode;
        if (_failure is not null)
        {
            RetryMeshRequestState.Write(context.Response, _failure);
        }
        return Task.CompletedTask;
    }

    public Task ExecuteResultAsync(ActionContext context) => ExecuteAsync(context.HttpContext);
}
