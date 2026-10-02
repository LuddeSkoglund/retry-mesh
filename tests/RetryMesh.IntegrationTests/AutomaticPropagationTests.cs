using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using RetryMesh;

namespace RetryMesh.IntegrationTests;

public class AutomaticPropagationTests
{
    [Theory]
    [InlineData("plain", 500, true)]
    [InlineData("success", 200, false)]
    [InlineData("recovered", 200, false)]
    [InlineData("replacement", 500, false)]
    [InlineData("mismatch", 500, false)]
    [InlineData("ambiguous", 500, false)]
    [InlineData("unique", 503, true)]
    [InlineData("exception-outer", 500, false)]
    [InlineData("exception-inner", 500, false)]
    [InlineData("exception-reexecute", 500, false)]
    [InlineData("explicit", 500, true)]
    [InlineData("explicit-mvc", 500, true)]
    [InlineData("strict", 500, false)]
    [InlineData("strict-explicit", 500, true)]
    [InlineData("success-first", 500, true)]
    [InlineData("parallel", 500, false)]
    [InlineData("transport", 500, false)]
    public async Task OutgoingResponseUsesConservativeRules(string scenario, int status, bool propagated)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (scenario.StartsWith("strict"))
            builder.Services.AddRetryMesh(o => o.PropagationMode = RetryMeshPropagationMode.Explicit);
        builder.Services.AddHttpClient("dependency").ConfigurePrimaryHttpMessageHandler(() => new Dependency())
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.Delay = TimeSpan.Zero;
                o.Retry.UseJitter = false;
                o.CircuitBreaker.MinimumThroughput = 100;
            }).UseRetryMesh("Proxy");
        await using var app = builder.Build();
        void HandleException() => app.UseExceptionHandler(handler => handler.Run(context =>
        {
            context.Response.StatusCode = 500;
            return Task.CompletedTask;
        }));
        if (scenario == "exception-outer") HandleException();
        if (scenario == "exception-reexecute") app.UseExceptionHandler("/error");
        app.UseRetryMesh();
        if (scenario == "exception-inner") HandleException();
        app.MapGet("/error", () => Results.StatusCode(500));
        app.MapGet("/", async (IHttpClientFactory clients, HttpContext context) =>
        {
            var client = clients.CreateClient("dependency");
            if (scenario == "success-first")
            {
                using var first = await client.GetAsync("http://dependency/200");
            }
            if (scenario == "parallel")
            {
                var responses = await Task.WhenAll(client.GetAsync("http://dependency/500"), client.GetAsync("http://dependency/500"));
                foreach (var item in responses) item.Dispose();
                return Results.StatusCode(500);
            }
            using var response = await client.GetAsync("http://dependency/" + (scenario switch
            {
                "success" => "200", "recovered" => "recover", "mismatch" => "503", _ => "500"
            }));
            if (scenario.StartsWith("exception")) throw new InvalidOperationException("Unrelated error");
            if (scenario is "replacement" or "unique" or "ambiguous")
            {
                using var replacement = await client.GetAsync("http://dependency/" + (scenario switch
                {
                    "replacement" => "200", "unique" => "503", _ => "500"
                }));
            }
            if (scenario == "transport")
            {
                try { using var ignored = await client.GetAsync("http://dependency/throw"); }
                catch (HttpRequestException) { }
            }
            if (scenario == "explicit-mvc")
            {
                await ((IActionResult)new RetryMeshFailureResult(response)).ExecuteResultAsync(new ActionContext { HttpContext = context });
                return Results.Empty;
            }
            return scenario is "explicit" or "strict-explicit"
                ? new RetryMeshFailureResult(response) : Results.StatusCode(status);
        });
        await app.StartAsync();
        using var caller = new HttpClient();
        using var result = await caller.GetAsync(app.Urls.Single());
        Assert.Equal(status, (int)result.StatusCode);
        Assert.Equal(propagated, RetryMeshHeaders.Read(result) is not null);
        if (propagated) Assert.Equal("Proxy", RetryMeshHeaders.Read(result)!.RetriedBy);
    }

    [Fact]
    public async Task ConcurrentIncomingRequestsHaveIsolatedCandidates()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHttpClient("dependency").ConfigurePrimaryHttpMessageHandler(() => new Dependency())
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.Delay = TimeSpan.Zero;
            }).UseRetryMesh("Proxy");
        await using var app = builder.Build();
        app.UseRetryMesh();
        var pendingFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successfulRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/{status:int}", async (int status, IHttpClientFactory clients) =>
        {
            if (status == 200) await pendingFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var downstream = await clients.CreateClient("dependency").GetAsync($"http://dependency/{status}");
            if (status == 500)
            {
                pendingFailure.SetResult();
                await successfulRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else successfulRequest.SetResult();
            return Results.StatusCode(status);
        });
        await app.StartAsync();
        using var client = new HttpClient();
        var responses = await Task.WhenAll(client.GetAsync(app.Urls.Single() + "/500"),
            client.GetAsync(app.Urls.Single() + "/200"));
        using var failed = responses[0];
        using var succeeded = responses[1];
        Assert.NotNull(RetryMeshHeaders.Read(failed));
        Assert.Null(RetryMeshHeaders.Read(succeeded));
    }

    private sealed class Dependency : HttpMessageHandler
    {
        private int _recoverCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            if (path == "throw") throw new HttpRequestException("Transport failure");
            var status = path == "recover" ? (Interlocked.Increment(ref _recoverCalls) < 3 ? 500 : 200) : int.Parse(path);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request });
        }
    }
}
