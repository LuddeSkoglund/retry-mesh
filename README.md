# RetryMesh

[![NuGet version](https://img.shields.io/nuget/vpre/RetryMesh.Http)](https://www.nuget.org/packages/RetryMesh.Http)
[![CI](https://github.com/LuddeSkoglund/retry-mesh/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/LuddeSkoglund/retry-mesh/actions/workflows/ci.yml)
[![MIT license](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/LuddeSkoglund/retry-mesh/blob/main/LICENSE)

**Retry locally. Propagate globally.**

Stop retry storms and retry amplification across .NET microservices. RetryMesh coordinates
HttpClient retries using **Microsoft.Extensions.Http.Resilience**, while keeping your existing resilience pipeline.

```text
Without RetryMesh                 With RetryMesh

A makes 3 attempts                A makes 1 attempt
└── B makes 3 attempts each        └── B makes 3 attempts
    └── C receives 9 requests         └── C receives 3 requests
```

B propagates retry exhaustion; A suppresses its additional retry loop.
**Three total attempts means one initial request + two retries**, not three retries plus the original.

Install the stable package after 0.1.0 has been published:

```sh
dotnet add package RetryMesh.Http
```

This branch prepares **0.1.0** for manual publication. To evaluate an already published preview,
use `dotnet add package RetryMesh.Http --prerelease`. To test the prepared stable package locally,
add `artifacts/packages` as a NuGet source and install version `0.1.0` from that source.

## Why RetryMesh?

Retries are usually configured locally. In `API → Orders → Payments`, both API and Orders may
independently perform three attempts. One logical operation can then create nine requests to
Payments exactly when the failing dependency is already under stress.

RetryMesh lets the service closest to the HTTP failure finish its configured retry policy,
then propagates exhaustion upstream so another service does not start an additional retry loop.

## Configure it

```csharp
using RetryMesh;

builder.Services.AddControllers();
builder.Services
    .AddHttpClient("Orders", client =>
    {
        client.BaseAddress = new Uri("https://orders.internal");
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2; // 1 initial request + 2 retries = 3 attempts
    })
    .UseRetryMesh(options =>
    {
        options.TrustDownstreamMetadata = true;
    });

var app = builder.Build();
app.UseExceptionHandler("/error"); // Your application's existing exception endpoint.
app.UseRouting();
// Existing authentication/authorization middleware goes here, if used.
app.UseRetryMesh();
app.MapControllers();
```

Call client `UseRetryMesh` **once, after retry configuration**. It registers the required services,
so `builder.Services.AddRetryMesh()` is optional. The service name defaults to the host's
`IHostEnvironment.ApplicationName`, or the entry assembly's simple name outside a host. Override
it with `.UseRetryMesh("CheckoutService", options => options.TrustDownstreamMetadata = true)`.
Names must contain 1–128 ASCII letters, digits, dots, underscores or hyphens; invalid inferred
names report an error rather than silently changing identity.

Existing MVC code remains ordinary:

```csharp
using var response = await httpClientFactory.CreateClient("Orders")
    .GetAsync("/api/orders", cancellationToken);
return StatusCode((int)response.StatusCode);
```

Minimal APIs can return `Results.StatusCode((int)response.StatusCode)`. Normal automatic
propagation requires no RetryMesh-specific result type. Typed clients work through the same
builder: use `AddHttpClient<OrdersClient>()`, where your client accepts an HttpClient.

## Internal services and external APIs

```text
A [internal] → B [internal] → C [external API]
```

C does not need to know about RetryMesh. Configure B's external client:

```csharp
builder.Services
    .AddHttpClient("ExternalPayments", client =>
        client.BaseAddress = new Uri("https://payments.example"))
    .AddStandardResilienceHandler(options => options.Retry.MaxRetryAttempts = 2)
    .UseRetryMesh(options => options.TrustDownstreamMetadata = false);
```

B performs its own retries and ignores RetryMesh-looking headers from C. When B exhausts its
own policy, it creates legitimate metadata describing B's retries. A can trust that metadata
from its internal dependency B:

```csharp
.UseRetryMesh(options => options.TrustDownstreamMetadata = true);
```

Trust defaults to **false**, per client. Headers are unauthenticated claims: `true` is appropriate
only when you trust the downstream service and communication path. RetryMesh does not know
whether an external provider retries internally.

## When should I use this?

Use RetryMesh when multiple HTTP services have nested retry policies, retry multiplication is
possible, and you use Microsoft.Extensions.Http.Resilience. Incremental adoption is supported.

It adds little value with a single retry boundary or no service-to-service retries. It does not
solve business-level idempotency or coordinate retries hidden inside an external provider.

## What RetryMesh is not

RetryMesh coordinates existing retry policies across service boundaries. It does not replace
**Microsoft.Extensions.Http.Resilience** or **Polly**, implement its own retry algorithm, or
implement timeouts and circuit breakers. Microsoft's standard handler retains retries, timeouts,
circuit breaking, rate limiting and telemetry; the existing predicate and OnRetry callback are preserved.

There is no Redis, database or central coordinator, and external APIs need no RetryMesh integration.

## Tested scenarios

The release gates validate real HTTP behavior:

- A → B → C: **9 → 3** calls; A → B → C → D: **27 → 3**.
- 100 concurrent roots: **100/100/300** A/B/C calls, with independent failure IDs.
- 1,000 sequential roots: **1,000/1,000/3,000**, without candidate accumulation between requests.
- Trusted internal metadata and ignored fake external claims.
- MVC, Minimal APIs, named and typed HttpClient.
- Actual packed-package consumers on .NET 8 and .NET 10, without ProjectReference.

**259 passing test executions** on Windows, including multi-target runs. The Windows/Linux CI
matrix runs the same gates; a local Linux run is not claimed. Details: [40-gate release report](https://github.com/LuddeSkoglund/retry-mesh/blob/main/docs/release-gate-0.1.0.md).
These are correctness tests, not throughput benchmarks.

## Current scope

RetryMesh coordinates **response-based HTTP failures**. DNS and connection failures, cancellation,
attempt timeouts, open circuits and limiter rejection without a response remain handled by
Microsoft resilience. RetryMesh does not fabricate HTTP exhaustion metadata for those outcomes.

Automatic propagation requires a final failure status with one matching pending exhausted
candidate. Status mismatches, ambiguous matching candidates, observed replacement successes
and ASP.NET exception handling prevent propagation.

**Matching status is not proof of causal identity.** If application code discards an exhausted
500 response and later returns an unrelated 500, automatic mode cannot distinguish them without
application participation. Application-caught exceptions and work through unconfigured clients
may also be invisible. An independent successful parallel branch completing after exhaustion
conservatively invalidates pending candidates. Await downstream work before starting the response.

For exact selected-response identity, opt into explicit mode:

```csharp
builder.Services.AddRetryMesh(options =>
    options.PropagationMode = RetryMeshPropagationMode.Explicit);
// In the endpoint selecting that failed response:
return new RetryMeshFailureResult(response);
```

The explicit helper supports MVC and Minimal APIs. Hedging, gRPC, queues and distributed retry
budgets are outside the supported scope. See [propagation rules and protocol](https://github.com/LuddeSkoglund/retry-mesh/blob/main/docs/propagation.md)
for exact middleware behavior, exception-handler ordering, protocol validation and explicit-result provenance.

## FAQ

**Does RetryMesh replace Polly?** No. It coordinates the existing retry policy; Polly remains underneath Microsoft's resilience handler.

**Does it replace Microsoft.Extensions.Http.Resilience?** No. It wraps the standard handler's retry decision and leaves the other strategies in place.

**Does every service need RetryMesh?** Only participating callers and failure propagators need it for coordination across their boundary. External leaf APIs do not.

**Does an external API need RetryMesh?** No. Your internal caller can create metadata after exhausting its own HTTP retry policy.

**What happens with partial adoption?** Normal resilience behavior continues. If B drops metadata or A does not understand it, A → B → C can still produce nine calls.

**Can an external API fake RetryMesh headers?** Yes, the headers are not authenticated. Untrusted clients ignore them by default and still perform local retries.

**Does it handle timeouts and network exceptions?** Microsoft resilience handles them. Without an HTTP response, RetryMesh cannot propagate exhaustion metadata.

**Why not disable retries everywhere except one service?** That can work when ownership is clear. RetryMesh coordinates existing local policies across changing service chains, while retaining ordinary behavior for non-participating dependencies.

## Run the proof

Install the .NET 10 SDK and .NET 8 ASP.NET Core runtime (or both SDKs), then:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
./scripts/demo.ps1
# Linux/macOS: bash scripts/demo.sh
```

The demo starts real MVC hosts on random localhost ports, asserts 9 → 3 and disposes them.
Samples use console logging without requiring Windows Event Log permissions. No Docker is required.
See [sample services](https://github.com/LuddeSkoglund/retry-mesh/tree/main/samples) for standalone hosting;
`RetryMesh:Enabled` switches coordination and `Downstream:BaseUrl` selects the dependency.

## Package and compatibility

```powershell
dotnet pack -c Release -o artifacts/packages
./scripts/release-gate-package.ps1 -SkipPack
```

The generated package is `artifacts/packages/RetryMesh.Http.0.1.0.nupkg`. It contains net8.0/net10.0
assemblies, this README and the MIT license, and depends on Microsoft.Extensions.Http.Resilience
10.0.0. Non-web consumers need the Microsoft.AspNetCore.App framework reference and runtime.
The package consumer script inspects metadata and runs independent .NET 8/.NET 10 applications.
It never publishes; published preview packages remain unchanged.

From preview.1, trusted internal clients must explicitly enable `TrustDownstreamMetadata = true`.
Service names and AddRetryMesh registration are now optional in normal setup; wire headers remain compatible.
[Release notes](https://github.com/LuddeSkoglund/retry-mesh/blob/main/docs/release-notes-0.1.0.md) describe manual GitHub upload.
For NuGet publication, sign in to NuGet.org, upload the `.nupkg`, review its metadata and publish it
manually. A GitHub release alone does not publish to NuGet.
