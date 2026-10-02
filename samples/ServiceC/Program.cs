await ServiceCHost.Build(args).RunAsync();

public sealed class RequestCounter
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public int Increment() => Interlocked.Increment(ref _count);
    public void Reset() => Interlocked.Exchange(ref _count, 0);
}

public static class ServiceCHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = typeof(ServiceCHost).Assembly.FullName });
        // Keep the standalone demo independent of Windows Event Log write permissions.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Services.AddSingleton<RequestCounter>();
        builder.Services.AddControllers();
        var app = builder.Build();
        // Optional fake external protocol claims for the trust-boundary demo.
        if (builder.Configuration.GetValue("FakeMetadata", false))
        {
            app.Use(async (context, next) =>
            {
                context.Response.Headers["RetryMesh-Status"] = "exhausted";
                context.Response.Headers["RetryMesh-Attempts"] = "99";
                context.Response.Headers["RetryMesh-By"] = "FakeExternal";
                context.Response.Headers["RetryMesh-Failure-Id"] = "fake";
                await next(context);
            });
        }
        app.MapControllers();
        app.MapGet("/stats", (RequestCounter counter) => Results.Ok(new { requestCount = counter.Count }));
        app.MapPost("/stats/reset", (RequestCounter counter) => { counter.Reset(); return Results.NoContent(); });
        return app;
    }
}

[Microsoft.AspNetCore.Mvc.ApiController]
[Microsoft.AspNetCore.Mvc.Route("fail")]
public sealed class ServiceCController(RequestCounter counter) : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [Microsoft.AspNetCore.Mvc.HttpGet]
    public Microsoft.AspNetCore.Mvc.IActionResult Get()
    {
        counter.Increment();
        return StatusCode(500);
    }
}
