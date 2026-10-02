using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using RetryMesh;

namespace RetryMesh.IntegrationTests;

public class ReleaseGateTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedConnectionPreservesThreeAttemptsAndExceptionHandling(bool enabled)
    {
        // Reserve a port without listening, so no other process can take the target port.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var attempts = 0;
        var callbacks = 0;
        var builder = NewHost();
        var pipeline = builder.Services.AddHttpClient("dependency")
            .ConfigurePrimaryHttpMessageHandler(() => new AttemptCounter(new SocketsHttpHandler { UseProxy = false }, () => Interlocked.Increment(ref attempts)))
            .AddStandardResilienceHandler(o => Configure(o, () => callbacks++));
        if (enabled) pipeline.UseRetryMesh("Proxy");
        await using var app = builder.Build();
        app.UseExceptionHandler(handler => handler.Run(c => { c.Response.StatusCode = 500; return Task.CompletedTask; }));
        if (enabled) app.UseRetryMesh();
        app.MapGet("/", async (IHttpClientFactory clients) =>
        {
            using var response = await clients.CreateClient("dependency").GetAsync($"http://127.0.0.1:{port}/");
            return Results.StatusCode((int)response.StatusCode);
        });
        await app.StartAsync();
        using var client = new HttpClient();
        using var result = await client.GetAsync(app.Urls.Single());
        Assert.Equal(500, (int)result.StatusCode);
        Assert.Equal(3, attempts);
        Assert.Equal(2, callbacks);
        Assert.Null(RetryMeshHeaders.Read(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeterministicDnsFailurePreservesMicrosoftBehavior(bool enabled)
    {
        var attempts = 0;
        var callbacks = 0;
        var sockets = new SocketsHttpHandler
        {
            UseProxy = false,
            // Inject the OS DNS failure at the real socket connection seam; no internet or DNS timing.
            ConnectCallback = (_, _) => throw new SocketException((int)SocketError.HostNotFound)
        };
        var services = new ServiceCollection();
        var pipeline = services.AddHttpClient("dependency")
            .ConfigurePrimaryHttpMessageHandler(() => new AttemptCounter(sockets, () => attempts++))
            .AddStandardResilienceHandler(o => Configure(o, () => callbacks++));
        if (enabled) pipeline.UseRetryMesh("Proxy");
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("dependency");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://release-gate.invalid/"));
        Assert.Equal(3, attempts);
        Assert.Equal(2, callbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSlowEndpointTimesOutThreeAttempts(bool enabled)
    {
        var incoming = 0;
        var callbacks = 0;
        await using var leaf = NewHost().Build();
        leaf.MapGet("/", async (HttpContext c) =>
        {
            Interlocked.Increment(ref incoming);
            await Task.Delay(Timeout.InfiniteTimeSpan, c.RequestAborted);
        });
        await leaf.StartAsync();
        var services = new ServiceCollection();
        var pipeline = services.AddHttpClient("dependency")
            .AddStandardResilienceHandler(o =>
            {
                Configure(o, () => callbacks++);
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
            });
        if (enabled) pipeline.UseRetryMesh("Proxy");
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("dependency");
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => client.GetAsync(leaf.Urls.Single()));
        Assert.Equal(3, incoming);
        Assert.Equal(2, callbacks);
    }

    [Fact]
    public async Task RootCancellationStopsRetriesAndNextRequestHasFreshState()
    {
        var incoming = 0;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var leaf = NewHost().Build();
        leaf.MapGet("/slow", async (HttpContext c) =>
        {
            Interlocked.Increment(ref incoming);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, c.RequestAborted); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); }
        });
        leaf.MapGet("/success", () => Results.Ok());
        await leaf.StartAsync();
        var attempts = 0;
        var callbacks = 0;
        var builder = NewHost();
        builder.Services.AddHttpClient("dependency", c => c.BaseAddress = new Uri(leaf.Urls.Single()))
            .ConfigurePrimaryHttpMessageHandler(() => new AttemptCounter(new SocketsHttpHandler(), () => Interlocked.Increment(ref attempts)))
            .AddStandardResilienceHandler(o => Configure(o, () => Interlocked.Increment(ref callbacks)))
            .UseRetryMesh("Proxy");
        await using var proxy = builder.Build();
        proxy.UseRetryMesh();
        proxy.MapGet("/{path}", async (string path, IHttpClientFactory clients, HttpContext context) =>
        {
            using var result = await clients.CreateClient("dependency").GetAsync("/" + path, context.RequestAborted);
            return Results.StatusCode((int)result.StatusCode);
        });
        await proxy.StartAsync();
        using var caller = new HttpClient();
        using var cancellation = new CancellationTokenSource();
        var pending = caller.GetAsync(proxy.Urls.Single() + "/slow", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, attempts);
        Assert.Equal(1, incoming);
        Assert.Equal(0, callbacks);
        using var success = await caller.GetAsync(proxy.Urls.Single() + "/success");
        Assert.Equal(200, (int)success.StatusCode);
        Assert.Null(RetryMeshHeaders.Read(success));
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenCircuitStopsRequestsWithoutFabricatedMetadata(bool enabled)
    {
        var incoming = 0;
        await using var leaf = NewHost().Build();
        leaf.MapGet("/", () => { Interlocked.Increment(ref incoming); return Results.StatusCode(500); });
        await leaf.StartAsync();
        var services = new ServiceCollection();
        var pipeline = services.AddHttpClient("dependency").AddStandardResilienceHandler(o =>
        {
            Configure(o, () => { });
            o.CircuitBreaker.MinimumThroughput = 2;
            o.CircuitBreaker.FailureRatio = 1;
            o.CircuitBreaker.BreakDuration = TimeSpan.FromMinutes(1);
        });
        if (enabled) pipeline.UseRetryMesh("Proxy");
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("dependency");
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.GetAsync(leaf.Urls.Single()));
        Assert.Equal(2, incoming);
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.GetAsync(leaf.Urls.Single()));
        Assert.Equal(2, incoming);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimiterStillRejectsConcurrentWork(bool enabled)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var incoming = 0;
        await using var leaf = NewHost().Build();
        leaf.MapGet("/", async () =>
        {
            Interlocked.Increment(ref incoming);
            entered.TrySetResult();
            await release.Task;
            return Results.Ok();
        });
        await leaf.StartAsync();
        var services = new ServiceCollection();
        var pipeline = services.AddHttpClient("dependency").AddStandardResilienceHandler(o =>
        {
            Configure(o, () => { });
            o.RateLimiter.DefaultRateLimiterOptions = new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 };
        });
        if (enabled) pipeline.UseRetryMesh("Proxy");
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("dependency");
        var first = client.GetAsync(leaf.Urls.Single());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<RateLimiterRejectedException>(() => client.GetAsync(leaf.Urls.Single()));
            Assert.Equal(1, incoming);
        }
        finally { release.TrySetResult(); }
        using var response = await first;
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Null(RetryMeshHeaders.Read(response));
    }

    internal static WebApplicationBuilder NewHost()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        return builder;
    }

    private static void Configure(HttpStandardResilienceOptions o, Action callback)
    {
        o.Retry.MaxRetryAttempts = 2;
        o.Retry.Delay = TimeSpan.Zero;
        o.Retry.UseJitter = false;
        o.CircuitBreaker.MinimumThroughput = 100;
        o.Retry.OnRetry = _ => { callback(); return default; };
    }

    private sealed class AttemptCounter(HttpMessageHandler inner, Action count) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            count();
            return base.SendAsync(request, cancellationToken);
        }
    }
}
