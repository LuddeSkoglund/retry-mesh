# DistributedResilience

Retry locally. Propagate globally.

Stop nested retry policies from amplifying failures across .NET microservices.

```text
Without DistributedResilience

A retries 3x
└── B retries 3x
    └── C

C receives 9 requests
```

```text
With DistributedResilience

A
└── B retries 3x
    └── C

A sees that downstream retries were exhausted
and does not retry the whole operation.

C receives 3 requests
```

**Proof of concept · .NET 10 · NuGet preview packaging**

Here, “3x” means **three total attempts: the original request plus two retries**.
The integration suite verifies both exact counts against the real sample applications.
Coordination prevents six unnecessary calls, a **66.7% reduction** in this chain.

## What this library does

DistributedResilience coordinates existing retry policies. It does not implement retries,
replace Polly, or replace `Microsoft.Extensions.Http.Resilience`. Microsoft’s standard
HTTP resilience handler still owns retries, timeouts, circuit breaking, rate limiting,
and telemetry. This library wraps only the retry decision and adds response metadata
when a specific downstream HTTP failure exhausts that policy.

There is no central server, database, Redis, message broker, or topology registry.
Services can adopt the protocol incrementally: services without coordination continue
using their normal policies. The full 9 → 3 benefit requires both B to propagate and A
to understand the metadata.

## Run the proof

Install the **.NET 10 SDK**. No Docker or external infrastructure is needed.

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

## Use the preview packages

Only `src/` projects are packable. Samples and tests never ship in NuGet packages.

```sh
dotnet pack -c Release -o artifacts/packages
```

This creates `DistributedResilience.Core.0.1.0-preview.1.nupkg` and
`DistributedResilience.Http.0.1.0-preview.1.nupkg`, each with this README embedded.
The HTTP package depends on Core and Microsoft’s resilience package. Consumers normally
need only **DistributedResilience.Http**. The packages have not been published to nuget.org.
To test them locally, add `artifacts/packages` as a NuGet source alongside nuget.org.

```sh
dotnet add package DistributedResilience.Http --version 0.1.0-preview.1
```

The HTTP package targets `net10.0` and references `Microsoft.AspNetCore.App` because it
includes the Minimal API propagation result. ASP.NET Core applications already have
this framework reference. Other consumers must add it and install the ASP.NET Core runtime.

## Register coordination

```csharp
using DistributedResilience;

builder.Services.AddDistributedResilience();

builder.Services
    .AddHttpClient("inventory", client =>
        client.BaseAddress = new Uri("https://inventory.internal"))
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2; // 1 original + 2 retries = 3 attempts
    })
    .UseDistributedRetries("CheckoutService");
```

Call `UseDistributedRetries` **once, after all retry configuration**. Its fluent receiver
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
        : (IResult)new DistributedFailureResult(downstream);
});
```

`DistributedFailureResult` snapshots the selected response’s status and validated metadata,
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
4. B explicitly propagates that failure with `DistributedFailureResult`.
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
Distributed-Retry-Status: exhausted
Distributed-Retry-Attempts: 3
Distributed-Retry-By: ServiceB
Distributed-Retry-Failure-Id: a4c4d483faeb4cc195fc75695fb78c91
```

| Header | Meaning |
| --- | --- |
| `Distributed-Retry-Status` | Currently only `exhausted` is supported. |
| `Distributed-Retry-Attempts` | Total local attempts, including the original; integer ≥ 2. |
| `Distributed-Retry-By` | Service that exhausted its policy. |
| `Distributed-Retry-Failure-Id` | Identity of the failure, preserved across upstream hops. |

All four headers are required and must have exactly one value. Service names and failure IDs
are limited to 128 ASCII letters, digits, dots, underscores, or hyphens. Missing, duplicate,
unknown, oversized, and malformed values are ignored. Successful responses never suppress
retries, even if they carry these headers. Constants and parsing live in `DistributedRetryHeaders`.

## Run the services yourself

Start each command in its own terminal, from the repository root:

```sh
dotnet run --project samples/ServiceC --no-launch-profile -- --urls=http://localhost:5103
dotnet run --project samples/ServiceB --no-launch-profile -- --urls=http://localhost:5102 --DistributedResilience:Enabled=false
dotnet run --project samples/ServiceA --no-launch-profile -- --urls=http://localhost:5101 --DistributedResilience:Enabled=false
```

Then:

```sh
curl -X POST http://localhost:5103/stats/reset
curl -i http://localhost:5101/execute
curl http://localhost:5103/stats
# {"requestCount":9}
```

Restart **A and B** with `--DistributedResilience:Enabled=true`, reset C, and repeat:
the counter is `3` and A’s 500 response contains exhaustion metadata from ServiceB.
Alternatively set `DistributedResilience__Enabled=true` in the environment or edit appsettings.
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

## Repository map

```text
src/DistributedResilience.Core/          Failure model; no infrastructure dependencies
src/DistributedResilience.Http/          Protocol, standard-handler extension, explicit IResult
samples/ServiceA/                        Upstream caller
samples/ServiceB/                        Downstream caller and failure propagator
samples/ServiceC/                        Always-failing endpoint and atomic counter
tests/DistributedResilience.Core.Tests/  Protocol and isolated HTTP pipeline tests
tests/DistributedResilience.IntegrationTests/  Real three-service scenarios
scripts/                                Automated comparison
.github/workflows/ci.yml                 Build, test, pack, and upload NuGet artifacts
```

## Limits and the path to NuGet v0.1

This preview proves HTTP response coordination, not a globally bounded retry system.

- **Trust:** headers are unauthenticated claims. Enable coordination only for trusted service
  responses; gateways must preserve the protocol. An untrusted peer can otherwise suppress retries.
- **Exceptions:** a final transport exception, cancellation, timeout, or open circuit has no
  downstream HTTP response to mark. These follow the existing pipeline’s behavior. Applications
  can map them to responses, but this preview does not claim exhaustion for those mappings.
- **Scope:** only the standard HTTP retry handler is integrated. Hedging, gRPC, queues,
  distributed budgets, retry ownership, and arbitrary exception graphs are outside this PoC.
- **Semantics:** applications choose whether they are forwarding the same failure. The library
  cannot infer causality after application code transforms or aggregates outcomes.
- **Target:** only .NET 10 is supported. The public API and wire format are provisional.

Before publishing a supported v0.1, choose a license and package ownership, review the public
API and protocol compatibility policy, define a trust-boundary policy, add broader cancellation,
timeout, concurrency, and framework compatibility tests, and decide whether the ASP.NET result
should remain in the HTTP package. Configure reproducible/source-linked builds and a release
workflow with NuGet credentials. This repository already builds local preview packages and
verifies the core behavior in CI; it does not publish automatically.
