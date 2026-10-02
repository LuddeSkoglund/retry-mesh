# RetryMesh

**Retry locally. Propagate globally.**

Stop nested retry policies from amplifying failures across .NET microservices.

```text
Without RetryMesh

A retries 3x
└── B retries 3x
    └── C

C receives 9 requests
```

```text
With RetryMesh

A
└── B retries 3x
    └── C

B propagates retry exhaustion.
A suppresses its additional retry loop.

C receives 3 requests.
```

**Preview 0.1.0-preview.1 · .NET 8 and .NET 10 · MIT license**

Here, “3x” means **three total attempts: the original request plus two retries**.
The integration suite verifies both exact counts against the real sample applications.
Coordination prevents six unnecessary calls, a **66.7% reduction** in this chain.

## What this library does

RetryMesh coordinates existing retry policies. It does not implement retries,
replace Polly, or replace `Microsoft.Extensions.Http.Resilience`. Microsoft’s standard
HTTP resilience handler still owns retries, timeouts, circuit breaking, rate limiting,
and telemetry. This library wraps only the retry decision and adds response metadata
when a specific downstream HTTP failure exhausts that policy.

There is no central server, database, Redis, message broker, or topology registry.
Services can adopt the protocol incrementally: services without coordination continue
using their normal policies. The full 9 → 3 benefit requires both B to propagate and A
to understand the metadata.

## Run the proof

Install the **.NET 10 SDK** plus the **.NET 8 ASP.NET Core runtime** to run the
multi-target test suite. Installing both SDKs also supplies the runtimes.
Samples target .NET 10; package and HTTP pipeline tests target .NET 8 and .NET 10.
No Docker or external infrastructure is needed.

```sh
dotnet restore
dotnet build
dotnet test
bash scripts/demo.sh
```

On Windows:

```powershell
./scripts/demo.ps1
```

The demo runs the two integration scenarios. Each scenario starts A, B, and C **in the
test process**, using real Kestrel HTTP endpoints on automatically assigned localhost
ports, resets C’s counter, executes one logical request, and asserts the exact count.
It disposes all hosts afterward. The script prints the comparison only if both assertions pass:

```text
Without coordination: ServiceC requests: 9
With coordination:    ServiceC requests: 3
Retry amplification prevented: 6 requests
Reduction: 66.7%
```

## Install

```sh
dotnet add package RetryMesh.Http --prerelease
```

There is exactly one package: **RetryMesh.Http**. It includes the failure model, HTTP
protocol, Microsoft resilience integration, and Minimal API propagation result.
The command above works once the preview is available on your configured NuGet feed.
This repository does not publish packages; see the local packaging instructions below.

The package targets **net8.0 and net10.0** and references `Microsoft.AspNetCore.App` because it
includes the Minimal API propagation result. ASP.NET Core applications already have
this framework reference. Other consumers must add it and install the ASP.NET Core runtime.

## Register coordination

```csharp
using RetryMesh;

builder.Services.AddRetryMesh();

builder.Services
    .AddHttpClient<InventoryClient>()
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2; // 1 original + 2 retries = 3 attempts
    })
    .UseRetryMesh("OrdersService");
```

`InventoryClient` is your application’s typed client accepting an `HttpClient` in its
constructor. Configure its base address for your trusted dependency. For a named client,
replace `AddHttpClient<InventoryClient>()` with
`AddHttpClient("inventory", client => client.BaseAddress = new Uri("https://inventory.internal"))`.

Call `UseRetryMesh` **once, after all retry configuration**. Its fluent receiver
is Microsoft’s `IHttpStandardResiliencePipelineBuilder`, returned by
`AddStandardResilienceHandler`. Both named and typed clients work through that builder.
The integration is pinned and tested against `Microsoft.Extensions.Http.Resilience` **10.0.0**.

### Propagate the same failure explicitly

```csharp
app.MapGet("/execute", async (IHttpClientFactory clients, HttpContext context) =>
{
    using var downstream = await clients.CreateClient("inventory")
        .GetAsync("/execute", context.RequestAborted);

    return downstream.IsSuccessStatusCode
        ? Results.Ok()
        : (IResult)new RetryMeshFailureResult(downstream);
});
```

`RetryMeshFailureResult` snapshots the selected response’s status and validated metadata,
so the response can be disposed before the result is executed. It returns an empty body
and forwards only protocol headers. It does not proxy arbitrary response headers or content.
The helper intentionally avoids middleware or ambient request state: an unrelated application
error must not inherit retry exhaustion from an earlier downstream call. Return an ordinary
`Results.StatusCode(500)` for such an error. No application code needs to inspect headers.

## How the decision works

1. B’s existing Microsoft retry policy sends three requests to C.
2. The wrapped `ShouldHandle` delegates to the original predicate for normal responses.
3. When that predicate identifies the final response as retryable and its zero-based
   attempt number equals `MaxRetryAttempts`, the library attaches exhaustion metadata
   to that particular `HttpResponseMessage`. No more retry is possible at this point.
4. B explicitly propagates that failure with `RetryMeshFailureResult`.
5. A’s wrapped predicate sees valid exhaustion metadata before making its own retry
   decision, preserves the downstream failure identity, and returns `false`.

There are no handwritten retry loops or extra delegating handlers. Responses are examined
inside the existing retry strategy, which avoids handler ordering ambiguity. Microsoft’s
remaining standard strategies retain their configuration and behavior. For example, circuit
breakers still observe failures and can open; coordination does not turn failures into success.

The tests verify that Polly evaluates the predicate on the final attempt, that a caller’s
custom retry predicate and `OnRetry` callback remain active, and that a successful or
non-retryable final response is never marked as exhausted.

## HTTP protocol v0.1

```http
RetryMesh-Status: exhausted
RetryMesh-Attempts: 3
RetryMesh-By: ServiceB
RetryMesh-Failure-Id: a4c4d483faeb4cc195fc75695fb78c91
```

| Header | Meaning |
| --- | --- |
| `RetryMesh-Status` | Currently only `exhausted` is supported. |
| `RetryMesh-Attempts` | Total local attempts, including the original; integer ≥ 2. |
| `RetryMesh-By` | Service that exhausted its policy. |
| `RetryMesh-Failure-Id` | Identity of the failure, preserved across upstream hops. |

All four headers are required and must have exactly one value. Service names and failure IDs
are limited to 128 ASCII letters, digits, dots, underscores, or hyphens. Missing, duplicate,
unknown, oversized, and malformed values are ignored. Successful responses never suppress
retries, even if they carry these headers. Constants and parsing live in `RetryMeshHeaders`.

## Run the services yourself

Start each command in its own terminal, from the repository root:

```sh
dotnet run --project samples/ServiceC --no-launch-profile -- --urls=http://localhost:5103
dotnet run --project samples/ServiceB --no-launch-profile -- --urls=http://localhost:5102 --RetryMesh:Enabled=false
dotnet run --project samples/ServiceA --no-launch-profile -- --urls=http://localhost:5101 --RetryMesh:Enabled=false
```

Then:

```sh
curl -X POST http://localhost:5103/stats/reset
curl -i http://localhost:5101/execute
curl http://localhost:5103/stats
# {"requestCount":9}
```

Restart **A and B** with `--RetryMesh:Enabled=true`, reset C, and repeat:
the counter is `3` and A’s 500 response contains exhaustion metadata from ServiceB.
Alternatively set `RetryMesh__Enabled=true` in the environment or edit appsettings.
`Downstream:BaseUrl` / `Downstream__BaseUrl` overrides each service’s downstream URL.

| Service | Endpoint | Behavior |
| --- | --- | --- |
| A | `GET /execute` | Calls B with three total attempts available. |
| B | `GET /execute` | Calls C with three total attempts available. |
| B | `GET /execute?unrelated=true` | Demonstrates an unrelated 500 without propagated metadata. |
| C | `GET /fail` | Atomically increments its counter and returns 500. |
| C | `GET /stats` | Returns the request counter. |
| C | `POST /stats/reset` | Atomically resets the counter. |

The samples log initial attempts, retries, C’s request numbers, exhaustion, failure identity,
and upstream suppression. Avoid concurrent demo traffic while resetting the shared sample counter.

### Windows logging permissions

The sample hosts explicitly use console logging. They do not write to Windows Event Log,
so `dotnet test` and the demo do not require administrator rights or Event Log source setup.
This also keeps Polly's warning/error telemetry visible without making retry execution depend
on machine-specific Event Log permissions. The NuGet library leaves logging providers under
the consuming application's control. CI runs the suite on both Windows and Linux.

## Repository map

```text
src/RetryMesh.Http/                 One package: model, protocol, integration, IResult
samples/ServiceA/                   Upstream caller
samples/ServiceB/                   Downstream caller and failure propagator
samples/ServiceC/                   Always-failing endpoint and atomic counter
tests/RetryMesh.Http.Tests/         Protocol and pipeline tests on .NET 8 and .NET 10
tests/RetryMesh.IntegrationTests/   Real three-service scenarios and logging regression
scripts/                           Automated comparison
.github/workflows/ci.yml            Windows/Linux build, test, pack, artifact upload
```

## Trust boundary and known limitations

This preview proves HTTP response coordination, not a globally bounded retry system.

- **Trust:** use RetryMesh on HttpClients representing trusted service-to-service dependencies.
  Headers are unauthenticated claims. Do not allow arbitrary third-party responses to control
  retry decisions. Gateways must preserve the protocol. Signatures and authentication are outside
  this preview; an untrusted peer could forge exhaustion metadata and suppress retries.
- **Transport:** preview.1 coordinates HTTP failures where a response exists and can carry
  metadata. Connection failures, DNS failures, transport exceptions, some timeouts, cancellation,
  and an already-open circuit breaker provide no response to mark. They follow Microsoft's
  existing behavior and are not coordinated. Mapping them to a 500 does not establish exhaustion.
- **Scope:** only the standard HTTP retry handler is integrated. Hedging, gRPC, queues,
  distributed budgets, retry ownership, and arbitrary exception graphs are outside this PoC.
- **Semantics:** applications choose whether they are forwarding the same failure. The library
  cannot infer causality after application code transforms or aggregates outcomes.
- **Compatibility:** .NET 8 and .NET 10 are supported. The public API and wire format are
  provisional. All participating services must understand the RetryMesh header names.

## Build a package for manual upload

From the repository root:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet pack -c Release -o ./artifacts
```

The uploadable file is:

```text
artifacts/RetryMesh.Http.0.1.0-preview.1.nupkg
```

Only the library is packable. The package contains both target-framework assemblies,
MIT metadata, the LICENSE, and this README. Its only package dependency is
`Microsoft.Extensions.Http.Resilience` (minimum version 10.0.0), with transitive dependencies
managed by NuGet. Samples, tests, and local artifacts are excluded. Generated packages and
smoke-test outputs are ignored by Git. CI builds and uploads this file as an Actions artifact
on Windows and Linux; it does not publish or create releases.

To consume the package locally, add the absolute path to `artifacts` as a NuGet source,
alongside nuget.org for Microsoft’s dependencies, then install the exact preview version:

```sh
dotnet add package RetryMesh.Http --version 0.1.0-preview.1
```

For manual publication, sign in to NuGet.org, choose **Upload Package**, select the `.nupkg`,
review its metadata and ownership, and publish it yourself. Availability of the package ID
and permissions for your NuGet account are checked by NuGet.org during upload. No API key,
publishing secret, tag, or automated release is needed by this repository.

Before a stable release, review API and protocol compatibility, add broader concurrency and
transport coverage, and evaluate the ASP.NET framework dependency. This remains a preview
of response-based retry coordination.
