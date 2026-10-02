using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

namespace RetryMesh;

public static class RetryMeshExtensions
{
    /// <summary>Registers logging used by the coordination layer.</summary>
    public static IServiceCollection AddRetryMesh(this IServiceCollection services)
    {
        services.AddLogging();
        return services;
    }

    /// <summary>Coordinates the standard handler's existing retry predicate. Call after configuring retries.</summary>
    public static IHttpStandardResiliencePipelineBuilder UseRetryMesh(
        this IHttpStandardResiliencePipelineBuilder builder, string serviceName)
    {
        if (!RetryMeshHeaders.ValidToken(serviceName))
            throw new ArgumentException("Use a service name of 1–128 ASCII letters, digits, dots, underscores or hyphens.", nameof(serviceName));

        return builder.Configure((options, services) =>
        {
            var normalDecision = options.Retry.ShouldHandle;
            var maxRetries = options.Retry.MaxRetryAttempts;
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("RetryMesh");
            options.Retry.ShouldHandle = async arguments =>
            {
                var response = arguments.Outcome.Result;
                if (response is not null && RetryMeshHeaders.Read(response) is { } downstream)
                {
                    logger.LogInformation("Upstream retry suppressed: downstream retries exhausted. FailureId={FailureId} RetriedBy={RetriedBy} Attempts={Attempts}",
                        downstream.FailureId, downstream.RetriedBy, downstream.Attempts);
                    return false;
                }

                var retryable = await normalDecision(arguments).ConfigureAwait(false);
                // Polly evaluates ShouldHandle for the last attempt too. Only that response is marked.
                if (retryable && response is not null && !response.IsSuccessStatusCode &&
                    maxRetries > 0 && arguments.AttemptNumber == maxRetries)
                {
                    var failure = new RetryMeshFailure
                    {
                        FailureId = Guid.NewGuid().ToString("N"), RetriedBy = serviceName,
                        Attempts = arguments.AttemptNumber + 1, Outcome = RetryMeshOutcome.Exhausted
                    };
                    RetryMeshHeaders.Write(response, failure);
                    logger.LogInformation("Downstream retries exhausted; failure available for propagation. FailureId={FailureId} RetriedBy={RetriedBy} Attempts={Attempts}",
                        failure.FailureId, failure.RetriedBy, failure.Attempts);
                }
                return retryable;
            };
        });
    }
}
