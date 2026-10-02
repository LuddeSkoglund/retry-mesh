using RetryMesh;
using Microsoft.Extensions.Http.Resilience;

await ServiceAHost.Build(args).RunAsync();

public static class ServiceAHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = typeof(ServiceAHost).Assembly.GetName().Name });
        // Keep the standalone demo independent of Windows Event Log write permissions.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Services.AddControllers();
        builder.Services.AddSingleton<ServiceACounter>();
        var enabled = builder.Configuration.GetValue("RetryMesh:Enabled", false);
        var retry = builder.Services.AddHttpClient("downstream", client =>
            client.BaseAddress = new Uri(builder.Configuration["Downstream:BaseUrl"] ?? "http://localhost:5102"))
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.Delay = TimeSpan.FromMilliseconds(10);
                options.Retry.UseJitter = false;
                options.CircuitBreaker.MinimumThroughput = 100;
                options.Retry.OnRetry = arguments =>
                {
                    Console.WriteLine("ServiceA: retry {0} (next attempt {1})", arguments.AttemptNumber + 1, arguments.AttemptNumber + 2);
                    return default;
                };
            });
        if (enabled) retry.UseRetryMesh(options => options.TrustDownstreamMetadata = true);
        var app = builder.Build();
        app.UseExceptionHandler(handler => handler.Run(context =>
        {
            context.Response.StatusCode = 500;
            return Task.CompletedTask;
        }));
        if (enabled) app.UseRetryMesh();
        app.MapControllers();
        return app;
    }
}

public sealed class ServiceACounter
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public void Increment() => Interlocked.Increment(ref _count);
}

[Microsoft.AspNetCore.Mvc.ApiController]
[Microsoft.AspNetCore.Mvc.Route("execute")]
public sealed class ServiceAController(IHttpClientFactory factory, ServiceACounter counter) : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [Microsoft.AspNetCore.Mvc.HttpGet]
    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> Get(CancellationToken cancellationToken)
    {
        counter.Increment();
        using var response = await factory.CreateClient("downstream").GetAsync("/execute", cancellationToken);
        if (Request.Query.ContainsKey("unrelated")) throw new InvalidOperationException("Unrelated application failure");
        return StatusCode((int)response.StatusCode);
    }
}
