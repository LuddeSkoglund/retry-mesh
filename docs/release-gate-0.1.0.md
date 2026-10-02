# RetryMesh 0.1.0 release gates

Validated on 2026-10-02 from merged `main`, commit `2a04021`, and revalidated for version `0.1.0` on branch `codex/release-0.1.0`, on Windows.

**RELEASE GATES PASSED FOR 0.1.0**, within the documented response-based and automatic-inference scope.
Version `0.1.0` is prepared locally for manual release. No NuGet publication or GitHub release was performed.

## Evidence and scope

- Final solution build: **0 errors, 0 warnings**.
- Final xUnit run: **259 passed, 0 failed, 0 skipped**: 86 HTTP tests on net8.0, the same 86 on net10.0, and 87 integration tests on net10.0. These are test executions, not 259 distinct cases.
- Two additional external **PackageReference-only consumers** built with warnings treated as errors and ran real three-host HTTP scenarios on **.NET 8.0.31** and **.NET 10.0.12**. Both produced A/B/C = **1/1/3**.
- Real Kestrel hosts use random loopback ports. MVC and Minimal API endpoint code uses ordinary status results, with no RetryMesh-specific result types.
- Expected statuses, counts and identities were encoded in the test cases before execution. Assertions were not relaxed to address failures.
- Load fixtures deliberately configure a high breaker threshold and sufficient limiter capacity, so isolation tests measure RetryMesh rather than an intentionally opened breaker. Separate tests exercise the actual breaker and limiter with and without RetryMesh.
- No library implementation changes were necessary. New tests, package automation, CI integration and documentation were added. Initial fixture/discovery, logging-filter and package-source setup errors were fixed in the test harness, not masked by changing expected product behavior.
- Windows validation is complete. The same tests and package consumer script are configured in the existing Windows/Linux CI matrix; a Linux run was not executed locally and is not claimed here.

## Release matrix

| Gate | Scenario | Expected | Actual | Result / notes |
| --- | --- | --- | --- | --- |
| 1 | A → B → C | Uncoordinated 1/3/9; coordinated 1/1/3 | Exact counts match | PASS; real hosts |
| 2 | A → B → C → D | 1/3/9/27 → 1/1/1/3 | Exact counts match | PASS; failure ID remains unchanged at each upstream hop |
| 3 | Recover before exhaustion | 200 after 1, 2 or 3 calls; no exhaustion | C = 1/2/3, A = B = 1, no metadata | PASS |
| 4 | Standard status predicate | Retry 500/502/503/429; no retry on ordinary 400/404 | Retryable cases C = 3 with metadata; 400/404 C = 1 without | PASS; original Microsoft decision preserved |
| 5 | Different final status | Exhausted 500 → 503, 503 → 500, 429 → 500 must not propagate | Three downstream calls in each; no metadata | PASS |
| 6 | Success after exhaustion | Final 200 must carry no exhaustion | No metadata, including successful replacement followed by a later 500 | PASS; successful replacement invalidates pending state |
| 7 | Unrelated application exception | Exception-handler 500 must not claim previous downstream 500 | No metadata with outer/inner handler or path re-execution | PASS; standard ASP.NET exception features, real hosts |
| 8 | Fake external claims | B ignores fake/99, retries 3 times, creates B/3; A suppresses | A/B/C = 1/1/3; ServiceB/3, fresh ID | PASS; dedicated real HTTP scenario |
| 9 | Trusted internal claims | Valid claims suppress; malformed claims use normal retries | Valid C = 3; malformed B = 3, C = 9; A creates its own legitimate metadata | PASS |
| 10 | Malformed protocol | Missing, empty, whitespace, duplicates, conflicts, bad tokens/counts ignored | 43 hostile inputs retain three local attempts and local identity | PASS; no parser exception |
| 11 | Ordinary MVC endpoints | C = 9 → 3 without controller-specific results | A/B/C = 1/3/9 → 1/1/3 | PASS; original samples and independent MVC fixture |
| 12 | Ordinary Minimal API endpoints | C = 9 → 3 using standard Results | Exact 1/3/9 → 1/1/3 | PASS |
| 13 | Named HttpClient | Full coordination through named client | C = 3; upstream attempts suppressed | PASS |
| 14 | Typed HttpClient | Same behavior through typed client | 9 → 3; MVC/typed and four-host/typed variants pass | PASS |
| 15 | 100 sequential root requests | A = B = 100, C = 300; unique IDs | Exact counts; 100 unique failure IDs | PASS |
| 16 | 100 concurrent root requests | A = B = 100, C = 300, independent identity | Exact counts and per-root 1/1/3; all 100 IDs unique and matched to B | PASS; no shared-state exception |
| 17 | Parallel downstream branches | Distinct 500/503 may select 503; two 500s must not guess | Unique matching ID propagated; ambiguous 500s rejected | PASS; success before exhaustion permits later unique failure; success after exhaustion conservatively invalidates it |
| 18 | Sequential downstream failures | 500 then 503 selects only 503; two 500s ambiguous | Selected ID matches returned status; duplicate matching candidates propagate nothing | PASS |
| 19 | Connection refused | Three attempts, two callbacks, unchanged exception handling, no fabricated metadata | Exact attempts/callbacks with and without RetryMesh; handler returns ordinary 500 without metadata | PASS; target port reserved but not listening |
| 20 | DNS failure | Same Microsoft retry behavior; no invented response | HttpRequestException, three attempts, two callbacks with/without RetryMesh | PASS; HostNotFound injected at SocketsHttpHandler.ConnectCallback, avoiding external DNS/network dependency |
| 21 | Attempt timeout | Three timed-out attempts; no fabricated HTTP exhaustion | Real slow endpoint gets three calls; TimeoutRejectedException; two callbacks | PASS; deliberately configured one-second attempt timeout |
| 22 | Root cancellation | Stop work and retries, preserve cancellation, fresh next request | One attempt, zero retries; leaf observes cancellation; next root succeeds without metadata | PASS; cancellation triggered after endpoint-entry signal |
| 23 | Open circuit | Breaker behavior unchanged; subsequent request sends nothing | Two initial downstream calls open circuit; third attempt and next request throw BrokenCircuitException; no extra calls | PASS |
| 24 | Custom retry predicate | Decorate original semantics | Custom 418 retryable = 3 calls; custom non-retryable 500 = 1 | PASS |
| 25 | OnRetry | Exactly one callback per actual retry | Two callbacks for three attempts; zero for one attempt; original callback preserved | PASS |
| 26 | Other strategies | Timeout, breaker and limiter stay active | Timeout and breaker gates pass; limiter with one permit rejects second concurrent operation | PASS; same behavior with/without RetryMesh |
| 27 | Retry count meaning | MaxRetryAttempts = 2 means 3 total calls | Exact three calls and two callbacks | PASS; README terminology audited |
| 28 | net8 packed consumer | PackageReference only, compile and run real code | Build: 0 warnings/errors; A/B/C = 1/1/3 | PASS; separate NuGet cache |
| 29 | net10 packed consumer | Same as net8 | Build: 0 warnings/errors; A/B/C = 1/1/3 | PASS; no ProjectReference |
| 30 | Package contents | Both TFMs, README, MIT, repository and dependencies; no private artifacts | Whitelisted package entries and metadata validated; consumers run successfully | PASS; no tests, samples, old identities, machine paths or temporary files |
| 31 | Public API | Consumer-facing API only | Eight exported types; middleware, state and validator internal | PASS; details below |
| 32 | Middleware order | Real pipeline results, no guessed ordering | Before routing, between routing/auth and after auth work; after terminal dispatch never executes; auth challenge sends zero downstream calls | PASS; exception-handler ordering also covered |
| 33 | No RetryMesh | Unrelated clients retain standard resilience | Plain client still retries valid-looking claims three times; coordinated trusted client makes one attempt | PASS; same service collection |
| 34 | Mixed adoption | Safe degradation, no unsupported inference | Missing A or missing B gives 1/3/9; A without RetryMesh emits no metadata; A with B missing can mark only its own exhaustion | PASS |
| 35 | Logging | Structured ILogger events; no Information noise on success | Local exhaustion, recording, propagation, suppression, untrusted claims and skip reasons captured; ordinary success emits no RetryMesh Information log | PASS; no library Console.WriteLine |
| 36 | Resource sanity | No global registry or growth across requests | 1,000 sequential roots give 1,000/1,000/3,000; unique IDs; each non-leaf request retains exactly one candidate; no static collection registry | PASS; sanity check, not a heap benchmark |
| 37 | Hostile header sizes | Oversized values and many duplicates ignored safely | 16 KiB IDs/service/count strings and 1,000 duplicate values retain normal retries | PASS; token cap 128, duplicate selection bounded to two values; transport buffers remain framework-owned |
| 38 | Success claims | 2xx headers must not suppress a custom retry decision | 200/204/299 with forged claims still make three attempts under a custom always-retry predicate; no exhaustion parsed | PASS |
| 39 | Failure identity | Upstream preserves the exhausted downstream ID | Same ID at every non-leaf hop, including four-service chain and concurrent per-root audits | PASS |
| 40 | Documentation | Clear setup, trust, scope, ordering, adoption and ambiguity | README audited and clarified for mixed adoption, routing/auth order and parallel completion | PASS |

## Findings and severity

| Severity | Finding | Resolution |
| --- | --- | --- |
| DOCUMENTATION | Partial adoption's exact fallback behavior needed a prominent explanation | Added both missing-A and missing-B examples; tested exact 1/3/9 |
| DOCUMENTATION | Routing/authentication/authorization placement was not explicit | Added recommended order and real-host placement/challenge tests |
| DOCUMENTATION | Independent successful parallel branches could be mistaken for a supported causal model | Documented completion-order false negatives and tested both deterministic orders |

**Library bugs discovered: 0. Library bugs requiring fixes: 0. Remaining BLOCKER: 0. Remaining HIGH: 0.**
No failure assertion was weakened. Existing product behavior and wire semantics were retained.

## Public API audit

| Exported type | Consumer surface / purpose |
| --- | --- |
| RetryMeshExtensions | AddRetryMesh(Action options); application UseRetryMesh(); client UseRetryMesh(Action options) and UseRetryMesh(string serviceName, Action options) |
| RetryMeshOptions | PropagationMode selects Automatic or Explicit |
| RetryMeshClientOptions | TrustDownstreamMetadata controls acceptance per client, default false |
| RetryMeshPropagationMode | Automatic, Explicit |
| RetryMeshFailure | FailureId, RetriedBy, Attempts, Outcome; immutable snapshot record and generated record members |
| RetryMeshOutcome | Exhausted; identifies the current protocol outcome |
| RetryMeshHeaders | Four header constants, Read(HttpResponseMessage), Write(HttpResponseMessage, RetryMeshFailure); useful for explicit interoperability |
| RetryMeshFailureResult | HttpResponseMessage constructor; ExecuteAsync(HttpContext), ExecuteResultAsync(ActionContext); implements IResult and IActionResult |

Middleware, feature state and options validation are internal. No new abstraction was exposed and no
gratuitous renaming was done. The trust boolean is explicit and readable; its authentication limitations
remain important. Automatic mode is a convenience policy, not a proof of failure causality. The existing
name-taking and options-taking overloads make a bare `UseRetryMesh(null)` call ambiguous; callers should
use a valid explicit name or omit the argument. Previously passing null was invalid configuration anyway.
The framework dependency on Microsoft.AspNetCore.App remains necessary for the combined package.

Full reflected members are emitted by the packed consumers into `public-api.json` under their generated
directories. The latest run directory is recorded in `artifacts/package-release-gate.json`.

## Remaining supported-scope limitations

- Automatic propagation cannot distinguish an unrelated application-generated failure with the same
  status after the downstream response identity is discarded. Explicit selection is needed for that guarantee.
- Application-caught exceptions, custom exception middleware hiding its error feature, and work through
  clients without RetryMesh may be invisible. Detached downstream work after response start is not supported.
- Independent successful parallel operations completing after exhaustion can suppress propagation.
  Two matching exhausted candidates are always rejected, even if their IDs are equal.
- Transport/DNS errors, cancellation, timeouts, open circuits and limiter rejection have no HTTP response
  to mark. Microsoft resilience handles them; RetryMesh does not fabricate a distributed exception protocol.
- Trust is per dependency and defaults false; headers are not signed or authenticated.
- Started responses cannot have metadata retroactively withdrawn after a later exception.
- The memory gate catches obvious per-request accumulation and static registries; it is not proof against
  every possible application retention pattern or a performance benchmark.

## Reproduce

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release --logger trx --results-directory artifacts/release-gate-results
dotnet pack -c Release -o artifacts/packages
./scripts/release-gate-package.ps1 -SkipPack
./scripts/demo.ps1
dotnet test tests/RetryMesh.IntegrationTests -c Release --no-build --filter 'FullyQualifiedName~ExactChainCountsAndIdentity'
```

TRX files under `artifacts/release-gate-results` contain the final run. The package consumer script validates
contents and creates unique external projects and an isolated package cache under `artifacts/package-consumers`.
The CI matrix runs the same package check on Windows and Linux. The inspected local package is
`artifacts/packages/RetryMesh.Http.0.1.0.nupkg`, prepared for manual upload. Previously saved
preview.3 version changes remain outside this release. No package was published by this validation.

The library is technically ready for the claimed response-based 0.1.0 scope according to these gates.
This conclusion does not remove the documented inference and transport limitations or substitute for
checking the configured cross-platform CI before release.
