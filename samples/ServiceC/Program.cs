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
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<RequestCounter>();
        var app = builder.Build();
        app.MapGet("/fail", (RequestCounter counter, ILoggerFactory logs) =>
        {
            logs.CreateLogger("ServiceC").LogInformation("ServiceC: request {RequestNumber}", counter.Increment());
            return Results.StatusCode(500);
        });
        app.MapGet("/stats", (RequestCounter counter) => Results.Ok(new { requestCount = counter.Count }));
        app.MapPost("/stats/reset", (RequestCounter counter) => { counter.Reset(); return Results.NoContent(); });
        return app;
    }
}
