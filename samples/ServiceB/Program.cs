using RetryMesh;
using Microsoft.Extensions.Http.Resilience;

await ServiceBHost.Build(args).RunAsync();

public static class ServiceBHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Keep the standalone demo independent of Windows Event Log write permissions.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Services.AddRetryMesh();
        var enabled = builder.Configuration.GetValue("RetryMesh:Enabled", false);
        var retry = builder.Services.AddHttpClient("downstream", client =>
            client.BaseAddress = new Uri(builder.Configuration["Downstream:BaseUrl"] ?? "http://localhost:5103"))
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.Delay = TimeSpan.FromMilliseconds(10);
                options.Retry.UseJitter = false;
                options.CircuitBreaker.MinimumThroughput = 100;
                options.Retry.OnRetry = arguments =>
                {
                    Console.WriteLine("ServiceB: retry {0} (next attempt {1})", arguments.AttemptNumber + 1, arguments.AttemptNumber + 2);
                    return default;
                };
            });
        if (enabled) retry.UseRetryMesh("ServiceB");
        var app = builder.Build();
        app.MapGet("/execute", async (IHttpClientFactory factory, ILoggerFactory logs, HttpContext context) =>
        {
            logs.CreateLogger("ServiceB").LogInformation("ServiceB: downstream attempt 1");
            using var response = await factory.CreateClient("downstream").GetAsync("/fail", context.RequestAborted);
            // Deliberately unrelated error: no downstream response is forwarded.
            if (context.Request.Query.ContainsKey("unrelated")) return Results.StatusCode(500);
            return response.IsSuccessStatusCode
                ? Results.Ok()
                : (IResult)new RetryMeshFailureResult(response);
        });
        return app;
    }
}
