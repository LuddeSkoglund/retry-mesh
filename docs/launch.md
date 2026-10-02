# Manual launch drafts

These are drafts for manual sharing **after 0.1.0 is published**. If sharing earlier, explicitly
call it a preview or a locally prepared release and use the prerelease installation command.
Counts below come from the release-gate correctness tests; they are not production benchmarks
or evidence of user adoption. Nothing in this document is posted automatically.

## Reddit / r/dotnet

**Suggested title:** I built a .NET library to reduce retry amplification across microservices — 9 downstream calls become 3

Retries help with transient failures, but they can multiply across a service boundary.

In `A → B → C`, suppose both A and B make three total attempts: one initial request plus two
retries. If C keeps returning HTTP 500, A can call B three times and each B call can call C three
times. C receives nine requests for one root operation, while it is already failing.

I built RetryMesh to coordinate that case using Microsoft.Extensions.Http.Resilience. B finishes
its existing local retry policy, propagates exhaustion metadata, and A suppresses its additional
retry loop. The real HTTP integration tests then give C three requests. A four-service chain
goes from 27 leaf calls to three.

There is no custom retry engine or central coordinator. Microsoft's standard handler keeps
timeouts, circuit breaking, rate limiting, retry callbacks and telemetry. MVC controllers and
Minimal API endpoints can keep returning ordinary status results; setup is on the client and middleware.

An external API does not need RetryMesh. Your internal caller retries it and creates its own
metadata after local exhaustion. Trust defaults to false per client, so arbitrary external
headers cannot stop your retries. Trusted internal clients can opt in; the headers are not authenticated.

The current scope is response-based HTTP failures. Network errors and timeouts without responses
remain Microsoft's responsibility. Automatic propagation also cannot prove identity when application
code discards a downstream response and later creates an unrelated error with the same status.
There is an explicit result option for that distinction.

I'd welcome feedback on the trust model, propagation tradeoffs and whether this matches retry
amplification you've seen in service chains.

GitHub: https://github.com/LuddeSkoglund/retry-mesh

NuGet: https://www.nuget.org/packages/RetryMesh.Http

## Hacker News / Show HN

**Suggested title:** Show HN: RetryMesh – preventing retry amplification in .NET microservices

RetryMesh coordinates existing Microsoft.Extensions.Http.Resilience policies across HTTP service
boundaries. With `A → B → C` and three total attempts at A and B, a persistent HTTP failure
normally produces nine calls to C. B finishes its local retries, propagates exhaustion metadata,
and A suppresses its extra loop: three calls in the real HTTP tests.

No custom retry engine, central coordinator or external API integration is required. Downstream
claims are untrusted by default. The current scope is HTTP responses; automatic same-status
failure identity remains an inference, with explicit propagation available.

Source and detailed tests: https://github.com/LuddeSkoglund/retry-mesh

Package: https://www.nuget.org/packages/RetryMesh.Http

## LinkedIn

`A → B → C`

Three total attempts at A × three at B = nine downstream calls to a failing C.
Those are two retries per layer, not three retries plus the original request.

I built RetryMesh to coordinate this with Microsoft.Extensions.Http.Resilience: the caller
closest to the HTTP failure finishes its retries and propagates exhaustion upstream. The real
HTTP tests show nine calls becoming three, with ordinary MVC and Minimal API endpoint code.

External APIs need no cooperation. Metadata trust is configured per client and defaults false.
Microsoft's handler continues to own retry execution, timeouts and circuit breaking.

It coordinates response-based failures, not network exceptions without a response or business
idempotency. Automatic propagation has documented failure-identity limits.

Source: https://github.com/LuddeSkoglund/retry-mesh

Package: https://www.nuget.org/packages/RetryMesh.Http

## dev.to / blog outline

**Title:** The hidden multiplication problem with retries in microservices

**Opening:** A retry policy can be reasonable in isolation and still amplify failures across
services. If API and Orders each make three attempts against their dependency, one persistent
Payments failure can trigger nine requests. The policies are doing what they were configured
to do; the missing piece is coordination between them.

1. **Why retries are useful:** transient HTTP failures, local policy decisions and the role of Microsoft resilience.
2. **How retries multiply across boundaries:** diagram `API → Orders → Payments` and identify both retry layers.
3. **3 × 3 = 9:** walk through one original request plus two retries at each layer; avoid ambiguous "3 retries" wording.
4. **Why outages can get worse:** extra traffic to a failing dependency; distinguish this example from a measured production benchmark.
5. **Retry locally, propagate globally:** finish retries closest to the failure, communicate exhaustion upstream.
6. **How RetryMesh works:** client predicate decoration, ordinary endpoints, middleware; Microsoft owns retry execution and other strategies.
7. **Trust boundaries:** conservative per-client defaults, unauthenticated metadata and trusted internal dependencies.
8. **External API example:** B ignores C's claims, performs local retries and creates legitimate B metadata that A may trust.
9. **Limitations:** response-based scope, same-status ambiguity, parallel success invalidation, partial adoption and explicit selection.
10. **Source and NuGet:** setup snippet, demo, release-gate report and links; publication status must match the actual feed.

Source: https://github.com/LuddeSkoglund/retry-mesh

Package: https://www.nuget.org/packages/RetryMesh.Http
