# RetryMesh

**Retry locally. Propagate globally.**

RetryMesh.Http **0.1.0-preview.2** coordinates Microsoft's existing HTTP retry policy across .NET 8 and .NET 10 services. Configure HttpClients, add middleware, and leave MVC controllers and Minimal API endpoints alone.

With two retries at A and B (three total attempts each), the real MVC A → B → C integration tests verify **A/B/C = 1/3/9 without coordination and 1/1/3 with RetryMesh**. The controllers contain no RetryMesh code.

## Automatic propagation: normal setup

Install the package from your configured NuGet feed. For this locally built preview,
add `artifacts` as a local NuGet source, then use:

```sh
dotnet add package RetryMesh.Http --version 0.1.0-preview.2
```

```csharp
using RetryMesh;

builder.Services
    .AddHttpClient<OrdersClient>()
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2;
    })
    .UseRetryMesh("CheckoutService", options =>
    {
        options.TrustDownstreamMetadata = true;
    });

var app = builder.Build();
app.UseExceptionHandler("/error"); // Configure your exception endpoint.
app.UseRetryMesh();                 // After exception handling, before endpoints.
app.MapControllers();
```

`OrdersClient` is your typed client accepting an HttpClient. Named clients also work:

```csharp
builder.Services.AddHttpClient("ServiceC", client =>
    client.BaseAddress = new Uri("http://localhost:5103"))
    .AddStandardResilienceHandler(options => options.Retry.MaxRetryAttempts = 2)
    .UseRetryMesh("ServiceB"); // Untrusted downstream by default.
```

Existing MVC controllers stay ordinary:

```csharp
[HttpGet]
public async Task<IActionResult> Get(CancellationToken cancellationToken)
{
    using var response = await httpClientFactory.CreateClient("ServiceC")
        .GetAsync("/fail", cancellationToken);
    return StatusCode((int)response.StatusCode);
}
```

Minimal APIs can return `Results.StatusCode((int)response.StatusCode)` in the same way.
No RetryMesh result is needed. Call the HttpClient `UseRetryMesh` once, **after all retry configuration**.
That call registers RetryMesh's services automatically, so the normal setup needs only the
client call and `app.UseRetryMesh()`. `AddRetryMesh(options => ...)` is optional, for changing
the propagation policy. Multiple clients do not duplicate core registrations or overwrite
explicit policy configuration, regardless of registration order.

RetryMesh wraps only `Retry.ShouldHandle`, preserving the original predicate and OnRetry callback.
There is no additional retry handler or handwritten retry loop. Microsoft.Extensions.Http.Resilience
10.0.0 retains responsibility for retries, timeouts, circuit breaking, rate limiting and telemetry.

## Trusted internal services and external APIs

Trust is configured **per HttpClient** and defaults to **false**:

```csharp
// A → internal B: accept B's exhaustion claims.
.UseRetryMesh("ServiceA", options => options.TrustDownstreamMetadata = true);

// B → external C: ignore C's claims, create metadata for B's own exhaustion.
.UseRetryMesh("ServiceB", options => options.TrustDownstreamMetadata = false);
```

`true` means another trusted RetryMesh service; `false` means external or untrusted.
Headers are unauthenticated claims. Use true only for a dependency whose responses and communication
path you trust. RetryMesh does not authenticate peers or sign metadata.

**External C needs no cooperation.** B retries C normally, creates local exhaustion metadata after
three failed attempts, and A can trust that claim from B. Creating local metadata and accepting
downstream claims are separate operations.

The tested A → B → fake external C scenario returns forged metadata claiming `FakeExternal`,
99 attempts. B removes and ignores those claims, performs three attempts, then creates legitimate
metadata for **ServiceB, 3 attempts**, with a new failure ID. A accepts B's claim and suppresses its
retries. C receives exactly **3 calls**. Removing untrusted protocol headers also prevents the
explicit result from forwarding those claims from a RetryMesh-enabled client.

## Automatic policy and limitations

Propagation defaults to `RetryMeshPropagationMode.Automatic`. This default serves common
proxy-style flows without controller changes. Registration and middleware opt the service into
conservative inference; they do **not** guarantee exact causal identity.

Middleware installs a private feature in `HttpContext.Features`. The retry predicate snapshots
failure ID, retrying service, attempt count and downstream status into that request's candidate
list. Response disposal does not affect the snapshot. IHttpContextAccessor finds the current
request from pooled client pipelines; there is no custom AsyncLocal or global candidate store.
Outside ASP.NET Core, client coordination still works, but no automatic response propagation occurs.

Immediately before headers start, `HttpResponse.OnStarting` requires:

- An outgoing failure status (400 or higher).
- Exactly one exhausted candidate matching that status. Multiple matching operations are ambiguous,
  even if their failure IDs match; nothing propagates.
- No invalidation. A successful or otherwise non-retryable downstream outcome after a pending failure
  permanently invalidates the request's candidates. An observed downstream exception also invalidates them.
- No exception handled by ASP.NET exception middleware replacing the outcome.
- No explicit result already selecting its own response.

Different exhausted statuses can coexist: 500 followed by 503 propagates only the unique 503
candidate when the final status is 503. Two exhausted 500 calls followed by 500 propagate nothing.
Successful final responses, status mismatches and retry recovery do not propagate metadata. A
successful replacement invalidates pending candidates even if application code later returns 500.

Place `app.UseRetryMesh()` **after exception handling and before endpoints**. It invalidates on
exceptions escaping downstream middleware and rethrows so the outer handler can render its response.
OnStarting also checks IExceptionHandlerFeature, covering an inner ASP.NET exception handler.
Exception-handler re-execution preserves invalidated state. Tests exercise both orders and path
re-execution. Custom middleware swallowing exceptions without the standard feature cannot be detected
if it runs inside RetryMesh.

**Fundamental ambiguity:** C exhausts with 500; application code then intentionally returns an
unrelated `StatusCode(500)`. Object identity has been discarded. Without observed invalidation,
automatic mode propagates the candidate. Middleware cannot distinguish those failures. Choose
explicit mode for aggregators or applications where this ambiguity is unacceptable. Application-caught
exceptions and successful work through clients without RetryMesh are also invisible to the tracker.

Candidate access is protected by a lock and multiple matching parallel failures are rejected.
This is not a general causal model for concurrent or detached work. Await downstream work before
starting the response. A response already started cannot be changed retroactively or have its
headers withdrawn after a later exception.

Coordination is **response-based**. Transport/DNS failures, cancellation, timeouts and open circuit
breakers without a response cannot create exhaustion metadata. Mapping them to 500 does not establish
exhaustion. Only the standard HTTP resilience retry strategy is supported; hedging, gRPC, queues,
distributed retry budgets and arbitrary exception graphs are outside this preview.

## Explicit failure identity

```csharp
builder.Services.AddRetryMesh(options =>
    options.PropagationMode = RetryMeshPropagationMode.Explicit);
```

Configure clients and middleware as above. Middleware writes no automatic metadata in this mode.
Select the actual downstream response explicitly:

```csharp
return new RetryMeshFailureResult(response);
```

The helper implements both `IResult` and `IActionResult`. It snapshots the chosen response's status
and validated metadata, works after disposal, returns an empty body and forwards only protocol
headers. It can also be used in automatic mode, where explicit selection takes precedence. The
caller is responsible for selecting the actual failure and its trusted provenance; the helper
itself does not authenticate raw downstream claims.

## HTTP protocol v0.1

```http
RetryMesh-Status: exhausted
RetryMesh-Attempts: 3
RetryMesh-By: ServiceB
RetryMesh-Failure-Id: a4c4d483faeb4cc195fc75695fb78c91
```

All four headers are required, each with exactly one value. Attempts must be an integer ≥ 2;
service names and failure IDs allow up to 128 ASCII letters, digits, dots, underscores or hyphens.
Missing, duplicate, malformed, unknown and oversized metadata is ignored. Successful responses
never suppress retries. Downstream status stays internal to candidate selection; no new wire
header is needed. Arbitrary headers and content are never proxied.

Trusted valid metadata suppresses retry and preserves downstream identity. Otherwise the original
predicate decides. On the last retryable failed HTTP response, RetryMesh creates local metadata
if at least one retry was configured. ILogger Information/Debug logs describe local exhaustion,
candidate recording, suppression, untrusted claims, propagation and skipped propagation reasons.
Logging providers belong to the consuming application.

## Run the proof

Install the .NET 10 SDK and .NET 8 ASP.NET Core runtime (or both SDKs).

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
./scripts/demo.ps1
# Linux/macOS: bash scripts/demo.sh
```

The demo starts real MVC sample hosts on temporary localhost ports, asserts exact counts and
disposes them. It prints 9 → 3 only after tests pass. Samples use console logging without requiring
Windows Event Log permissions. There is no Docker or external infrastructure requirement.

Run manually in three terminals from the repository root:

```sh
dotnet run --project samples/ServiceC --no-launch-profile -- --urls=http://localhost:5103
dotnet run --project samples/ServiceB --no-launch-profile -- --urls=http://localhost:5102 --RetryMesh:Enabled=false
dotnet run --project samples/ServiceA --no-launch-profile -- --urls=http://localhost:5101 --RetryMesh:Enabled=false
```

POST `http://localhost:5103/stats/reset`, GET `http://localhost:5101/execute`, then GET
`http://localhost:5103/stats`: 9 calls. Restart A and B with `--RetryMesh:Enabled=true`, reset
and repeat: 3 calls and ServiceB metadata. `Downstream:BaseUrl` overrides each dependency URL.
ServiceB `/execute?unrelated=true` throws after downstream exhaustion and returns 500 without
RetryMesh metadata through its exception handler. ServiceC `--FakeMetadata=true` enables the
forged external claims demonstration.

## Package and compatibility

```powershell
dotnet pack -c Release -o artifacts
```

Generated package: `artifacts/RetryMesh.Http.0.1.0-preview.2.nupkg`. Only the library is packable.
It contains net8.0/net10.0 assemblies, README and MIT license, and depends on
Microsoft.Extensions.Http.Resilience 10.0.0. It references Microsoft.AspNetCore.App; non-web
consumers need that framework reference and runtime. Nothing is published by this change.
Preview.1 remains unchanged on NuGet.

Changes from preview.1: downstream claims require explicit trust; HttpClient UseRetryMesh automatically
registers request access and defaults to automatic propagation when middleware is installed; RetryMeshFailureResult
also supports MVC. Existing service-name overloads and wire headers remain compatible, but previously
implicit trusted-client behavior requires `TrustDownstreamMetadata = true`. The public API is provisional.
