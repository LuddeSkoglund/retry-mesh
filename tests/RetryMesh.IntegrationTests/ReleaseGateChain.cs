using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetryMesh;

namespace RetryMesh.IntegrationTests;

// Real sockets, separate hosts and pooled clients. Expected counts live in tests, not this fixture.
internal sealed class ReleaseGateChain : IAsyncDisposable
{
    internal List<WebApplication> Hosts { get; } = [];
    internal List<GateNode> Nodes { get; } = [];
    internal string Url => Hosts[0].Urls.Single();

    internal static async Task<ReleaseGateChain> Start(int length = 3, bool enabled = true,
        bool mvc = false, bool typed = false, int terminalStatus = 500, int recoverAt = 0,
        bool fakeExternal = false, bool malformedInternal = false, int? unconfiguredNode = null, ILoggerProvider? logs = null)
    {
        var chain = new ReleaseGateChain();
        try
        {
            for (var index = length - 1; index >= 0; index--)
            {
                var leaf = index == length - 1;
                var node = new GateNode
                {
                    Name = $"Service{(char)('A' + index)}", Leaf = leaf, Status = terminalStatus,
                    RecoverAt = recoverAt, Typed = typed, FakeExternal = leaf && fakeExternal,
                    MalformedMetadata = malformedInternal && index == 1
                };
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ApplicationName = typeof(GateController).Assembly.GetName().Name,
                    EnvironmentName = "Production"
                });
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                if (logs is not null) builder.Logging.AddProvider(logs).AddFilter("RetryMesh", LogLevel.Debug);
                builder.Services.AddSingleton(node);
                if (mvc) builder.Services.AddControllers().AddApplicationPart(typeof(GateController).Assembly);
                if (!leaf)
                {
                    var downstream = chain.Hosts[0].Urls.Single();
                    var client = typed ? builder.Services.AddHttpClient<GateClient>(c => c.BaseAddress = new Uri(downstream))
                        : builder.Services.AddHttpClient("dependency", c => c.BaseAddress = new Uri(downstream));
                    var pipeline = client.AddStandardResilienceHandler(options =>
                    {
                        options.Retry.MaxRetryAttempts = 2;
                        options.Retry.Delay = TimeSpan.Zero;
                        options.Retry.UseJitter = false;
                        // Load gates measure RetryMesh isolation, not an intentionally opened breaker.
                        options.CircuitBreaker.MinimumThroughput = 100_000;
                        options.RateLimiter.DefaultRateLimiterOptions.PermitLimit = 2000;
                        options.RateLimiter.DefaultRateLimiterOptions.QueueLimit = 2000;
                        options.Retry.OnRetry = _ => { Interlocked.Increment(ref node.RetryCallbacks); return default; };
                    });
                    if (enabled && index != unconfiguredNode)
                        pipeline.UseRetryMesh(node.Name, o => o.TrustDownstreamMetadata = index < length - 2);
                }
                var app = builder.Build();
                // Audit after RetryMesh OnStarting (callbacks run in reverse registration order).
                app.Use(async (context, next) =>
                {
                    var root = context.Request.Query["root"].ToString();
                    var count = Interlocked.Increment(ref node.Incoming);
                    node.RootCounts.AddOrUpdate(root, 1, (_, n) => n + 1);
                    context.Items["attempt"] = count;
                    context.Response.OnStarting(() =>
                    {
                        if (node.FakeExternal)
                        {
                            context.Response.Headers[RetryMeshHeaders.Status] = "exhausted";
                            context.Response.Headers[RetryMeshHeaders.Attempts] = "99";
                            context.Response.Headers[RetryMeshHeaders.RetriedBy] = "FakeExternalService";
                            context.Response.Headers[RetryMeshHeaders.FailureId] = "fake";
                        }
                        if (node.MalformedMetadata) context.Response.Headers[RetryMeshHeaders.Attempts] = "abc";
                        if (context.Response.Headers.TryGetValue(RetryMeshHeaders.FailureId, out var id))
                            node.FailureIds[root] = id.ToString();
                        return Task.CompletedTask;
                    });
                    await next(context);
                    // Resource sanity: inspect only after all endpoint work completes, retain counts, not contexts.
                    var state = context.Features.FirstOrDefault(f => f.Key.FullName == "RetryMesh.RetryMeshRequestState").Value;
                    if (state is not null)
                    {
                        var candidates = (System.Collections.ICollection)state.GetType().GetField("_candidates",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(state)!;
                        node.CandidateSizes.Add(candidates.Count);
                    }
                    node.Completed.Release();
                });
                if (enabled && !leaf && index != unconfiguredNode) app.UseRetryMesh();
                if (mvc) app.MapControllers();
                else app.MapGet("/execute", async (GateNode state, HttpContext context) =>
                    Results.StatusCode(await GateEndpoint.Execute(state, context)));
                await app.StartAsync();
                chain.Hosts.Insert(0, app);
                chain.Nodes.Insert(0, node);
            }
            return chain;
        }
        catch { await chain.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in Hosts) await host.DisposeAsync();
    }
}

public sealed class GateNode
{
    public required string Name { get; init; }
    public bool Leaf { get; init; }
    public bool Typed { get; init; }
    public bool FakeExternal { get; init; }
    public bool MalformedMetadata { get; init; }
    public int Status { get; init; }
    public int RecoverAt { get; init; }
    public int Incoming;
    public int RetryCallbacks;
    public ConcurrentDictionary<string, int> RootCounts { get; } = new();
    public ConcurrentDictionary<string, string> FailureIds { get; } = new();
    public ConcurrentBag<int> CandidateSizes { get; } = [];
    public SemaphoreSlim Completed { get; } = new(0);
}

public sealed class GateClient(HttpClient client)
{
    public Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken) => client.GetAsync(path, cancellationToken);
}

internal static class GateEndpoint
{
    internal static async Task<int> Execute(GateNode node, HttpContext context)
    {
        if (node.Leaf)
            return node.RecoverAt > 0 && (int)context.Items["attempt"]! >= node.RecoverAt ? 200 : node.Status;
        var path = "/execute" + context.Request.QueryString;
        using var response = node.Typed
            ? await context.RequestServices.GetRequiredService<GateClient>().GetAsync(path, context.RequestAborted)
            : await context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("dependency").GetAsync(path, context.RequestAborted);
        return (int)response.StatusCode;
    }
}

[ApiController]
[Route("execute")]
public sealed class GateController(GateNode node) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get() => StatusCode(await GateEndpoint.Execute(node, HttpContext));
}
