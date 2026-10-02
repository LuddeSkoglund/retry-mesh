using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace RetryMesh;

public static class RetryMeshExtensions
{
    /// <summary>Optionally configures propagation policy. HttpClient UseRetryMesh registers services automatically.</summary>
    public static IServiceCollection AddRetryMesh(this IServiceCollection services, Action<RetryMeshOptions>? configure = null)
    {
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddOptions<RetryMeshOptions>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RetryMeshOptions>, RetryMeshOptionsValidator>());
        if (configure is not null) services.Configure(configure);
        return services;
    }

    /// <summary>Place after exception handling and before endpoints to track outgoing failures.</summary>
    public static IApplicationBuilder UseRetryMesh(this IApplicationBuilder app) => app.UseMiddleware<RetryMeshMiddleware>();

    /// <summary>Registers RetryMesh services and coordinates the standard handler's existing retry predicate. Call after configuring retries.</summary>
    public static IHttpStandardResiliencePipelineBuilder UseRetryMesh(
        this IHttpStandardResiliencePipelineBuilder builder, string serviceName, Action<RetryMeshClientOptions>? configure = null)
    {
        if (!RetryMeshHeaders.ValidToken(serviceName))
            throw new ArgumentException("Use a service name of 1–128 ASCII letters, digits, dots, underscores or hyphens.", nameof(serviceName));

        var clientOptions = new RetryMeshClientOptions();
        configure?.Invoke(clientOptions);
        var trustDownstream = clientOptions.TrustDownstreamMetadata;
        builder.Services.AddRetryMesh();
        return builder.Configure((options, services) =>
        {
            var normalDecision = options.Retry.ShouldHandle;
            var maxRetries = options.Retry.MaxRetryAttempts;
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("RetryMesh");
            var accessor = services.GetService<IHttpContextAccessor>();
            options.Retry.ShouldHandle = async arguments =>
            {
                var response = arguments.Outcome.Result;
                var state = accessor?.HttpContext?.Features.Get<RetryMeshRequestState>();
                if (response is null) state?.Invalidate();
                if (response is not null && !trustDownstream)
                {
                    if (response.Headers.Contains(RetryMeshHeaders.Status))
                        logger.LogDebug("RetryMesh downstream metadata ignored: untrusted dependency. Service={Service}", serviceName);
                    // Remove claims so explicit propagation cannot accidentally forward untrusted metadata.
                    foreach (var name in new[] { RetryMeshHeaders.Status, RetryMeshHeaders.Attempts, RetryMeshHeaders.RetriedBy, RetryMeshHeaders.FailureId })
                        response.Headers.Remove(name);
                }
                if (trustDownstream && response is not null && RetryMeshHeaders.Read(response) is { } downstream)
                {
                    state?.Record(downstream, (int)response.StatusCode);
                    logger.LogDebug("RetryMesh failure candidate recorded. FailureId={FailureId} StatusCode={StatusCode}", downstream.FailureId, (int)response.StatusCode);
                    logger.LogInformation("Upstream retry suppressed: downstream retries exhausted. FailureId={FailureId} RetriedBy={RetriedBy} Attempts={Attempts}",
                        downstream.FailureId, downstream.RetriedBy, downstream.Attempts);
                    return false;
                }

                var retryable = await normalDecision(arguments).ConfigureAwait(false);
                if (response is not null && (!retryable || response.IsSuccessStatusCode))
                    state?.Invalidate(onlyIfPending: true);
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
                    state?.Record(failure, (int)response.StatusCode);
                    logger.LogDebug("RetryMesh failure candidate recorded. FailureId={FailureId} StatusCode={StatusCode}", failure.FailureId, (int)response.StatusCode);
                    logger.LogInformation("RetryMesh local retries exhausted. FailureId={FailureId} RetriedBy={RetriedBy} Attempts={Attempts}",
                        failure.FailureId, failure.RetriedBy, failure.Attempts);
                }
                return retryable;
            };
        });
    }
}
