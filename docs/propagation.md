# Propagation policy and HTTP protocol

RetryMesh coordinates response-based HTTP failures through Microsoft.Extensions.Http.Resilience.
The normal setup is shown in [the README](../README.md). Recommended pipeline order: exception
handler, routing, existing authentication/authorization, RetryMesh, then endpoints.

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
Routing and authentication/authorization can run before RetryMesh; this is the recommended order
shown above. Real-host tests also cover RetryMesh before routing or authentication without false
propagation. Middleware placed after terminal endpoint dispatch never executes, so automatic
propagation is unavailable. Keep authentication/authorization ahead of endpoint execution.

**Fundamental ambiguity:** C exhausts with 500; application code then intentionally returns an
unrelated `StatusCode(500)`. Object identity has been discarded. Without observed invalidation,
automatic mode propagates the candidate. Middleware cannot distinguish those failures. Choose
explicit mode for aggregators or applications where this ambiguity is unacceptable. Application-caught
exceptions and successful work through clients without RetryMesh are also invisible to the tracker.

Candidate access is protected by a lock and multiple matching parallel failures are rejected.
This is not a general causal model for concurrent or detached work. Await downstream work before
starting the response. A response already started cannot be changed retroactively or have its
headers withdrawn after a later exception.
For parallel branches, a success completing after exhaustion invalidates pending candidates even
when the success belongs to an independent branch. A success completing before the exhausted
candidate does not invalidate that later candidate. This deliberately conservative completion
rule can cause false negatives; use explicit selection when branch identity matters.

Coordination is **response-based**. Transport/DNS failures, cancellation, timeouts and open circuit
breakers without a response cannot create exhaustion metadata. Mapping them to 500 does not establish
exhaustion. Only the standard HTTP resilience retry strategy is supported; hedging, gRPC, queues,
distributed retry budgets and arbitrary exception graphs are outside this release.

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

