using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RetryMesh;

internal sealed class RetryMeshMiddleware(RequestDelegate next, IOptions<RetryMeshOptions> options,
    ILogger<RetryMeshMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Preserve the same state on exception-handler re-execution.
        var state = context.Features.Get<RetryMeshRequestState>();
        if (state is null)
        {
            state = new RetryMeshRequestState();
            context.Features.Set(state);
            context.Response.OnStarting(() =>
            {
                if (state.ExplicitResult || options.Value.PropagationMode != RetryMeshPropagationMode.Automatic)
                    return Task.CompletedTask;
                if (context.Features.Get<IExceptionHandlerFeature>() is not null) state.Invalidate();
                var selected = state.Select(context.Response.StatusCode);
                if (selected.Failure is { } failure)
                {
                    RetryMeshRequestState.Write(context.Response, failure);
                    logger.LogInformation("RetryMesh metadata propagated automatically. FailureId={FailureId} RetriedBy={RetriedBy} Attempts={Attempts}",
                        failure.FailureId, failure.RetriedBy, failure.Attempts);
                }
                else logger.LogDebug("RetryMesh propagation skipped: {Reason}", selected.Reason);
                return Task.CompletedTask;
            });
        }
        try { await next(context); }
        catch
        {
            state.Invalidate();
            throw;
        }
    }
}
