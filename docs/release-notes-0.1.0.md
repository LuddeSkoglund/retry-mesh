# RetryMesh.Http 0.1.0

First stable release of response-based retry coordination for .NET 8 and .NET 10.

**Retry locally. Propagate globally.**

RetryMesh coordinates Microsoft's existing HTTP resilience retry policy to reduce nested retry
amplification. With two retries per service, real HTTP tests show 9 → 3 calls in A → B → C and
27 → 3 in A → B → C → D.

## Included

- Automatic HTTP exhaustion propagation for ordinary MVC controllers and Minimal APIs.
- Compact setup: `.AddStandardResilienceHandler(...).UseRetryMesh(...)` and `app.UseRetryMesh()`.
- Optional service name, inferred from the host application or entry assembly.
- Per-client `TrustDownstreamMetadata`, default false; external API headers cannot disable local retries.
- Explicit propagation through `RetryMeshFailureResult` for MVC and Minimal APIs.
- Defensive protocol parsing, request-local snapshots and structured ILogger events.
- Existing Microsoft retry predicates, callbacks, timeouts, circuit breakers and rate limiting remain active.

## Validation

259 passing test executions, with no build warnings or errors. Real Kestrel scenarios include MVC,
Minimal APIs, named/typed clients, 100 concurrent requests and 1,000 sequential requests. The packed
package is consumed and run by separate net8.0/net10.0 applications without ProjectReference.
See `docs/release-gate-0.1.0.md` for the 40-gate matrix and validation scope.

## Important limits

Automatic mode infers failure identity from conservative response rules. It cannot prove that an
unrelated application failure with the same status is the original downstream failure after response
identity is discarded. Use explicit selection when that distinction matters. Successful parallel
branches can conservatively invalidate pending candidates.

Coordination requires an HTTP response. Transport/DNS failures, cancellation, timeout, open circuits
and limiter rejection remain Microsoft's responsibility and do not create fabricated exhaustion
metadata. Downstream claims are not authenticated; enable trust only for dependencies you trust.

Configure RetryMesh middleware after exception handling and before endpoint execution; the recommended
order places existing routing/authentication/authorization before RetryMesh.

## Preview migration

Compared with preview.1, explicitly enable `TrustDownstreamMetadata = true` for trusted internal
dependencies. `AddRetryMesh()` and a literal service name are optional in normal client registration.
The HTTP wire headers remain unchanged. Non-web consumers still require Microsoft.AspNetCore.App.

## Manual GitHub upload

After merging the release PR, create a GitHub release with tag `v0.1.0` targeting the merged main
commit. Use the notes above and upload `RetryMesh.Http.0.1.0.nupkg` and its `.sha256` file; the ZIP
bundle can also be attached. Publishing a GitHub release does not publish to NuGet.
