# GitHub and NuGet discoverability

## Set manually in GitHub

Open the repository's **About** settings and use this description:

> Stop retry storms across .NET microservices. Retry locally, propagate globally.

Recommended topics:

```text
dotnet, csharp, aspnetcore, resilience, retry, retry-storm, retry-amplification,
microservices, httpclient, distributed-systems, polly
```

Set the website to `https://www.nuget.org/packages/RetryMesh.Http`.
The repository is public; its description was empty and no topics were configured when checked
on 2026-10-02. These are recommendations only; no repository settings have been changed.

## Package search metadata

Package ID and title: **RetryMesh.Http**. Prepared version: **0.1.0**.

Exact description:

> Prevent retry storms and retry amplification across .NET microservices. Coordinate retries between services using Microsoft.Extensions.Http.Resilience without replacing your existing resilience pipeline.

Exact PackageTags value:

```text
retry;retries;retry-storm;retry-amplification;resilience;microservices;distributed-systems;http;httpclient;aspnetcore;dotnet;polly;microsoft-extensions-http-resilience
```

The packed nuspec is checked for the ID, title, description, tags, version, MIT license,
repository and framework/dependency metadata. The README is packed without changing core behavior.

## Badges checked on 2026-10-02

| Badge | Endpoint | Observed result |
| --- | --- | --- |
| Repository package version | `https://img.shields.io/badge/version-0.1.0-blue.svg` | HTTP 200 SVG; `0.1.0`, matching the project version |
| Main branch CI | `https://github.com/LuddeSkoglund/retry-mesh/actions/workflows/ci.yml/badge.svg?branch=main` | HTTP 200 SVG; passing |
| MIT license | `https://img.shields.io/badge/license-MIT-blue.svg` | HTTP 200 SVG; MIT |

The README version badge shows the stable package version prepared in this repository and links
to its release notes. Update it when the project version changes. It does not claim that the
package has been published to NuGet. NuGet's version index contained preview.1, preview.2 and
preview.3; **0.1.0 was not published** at this check. Both the `nuget/vpre` and `nuget/v` endpoints
returned `v0.1.0-preview.3` while no stable version was available, so changing only that endpoint
would still display a preview. After publishing 0.1.0, a separate published-version badge can use
`https://img.shields.io/nuget/v/RetryMesh.Http` and link to the NuGet package page.
No download badge or hardcoded count was added. Main's CI badge is not a claim about release-branch
checks. The MIT badge is supported by the repository license and package license expression.

## Manual publication checklist

1. Merge the release PR and review the configured Windows/Linux CI results.
2. Upload the verified `artifacts/packages/RetryMesh.Http.0.1.0.nupkg` to NuGet.org and review its metadata.
3. Publish manually; then confirm the stable package is available before using the stable install command in launch posts.
4. Set the repository description, topics and website above.
5. Choose any relevant draft in [launch.md](launch.md) and edit it for the community before posting manually.

The primary stable installation command is `dotnet add package RetryMesh.Http`. Until stable
publication, use `--prerelease` to evaluate published previews or configure the local package folder.
No publication, repository-setting update, GitHub release or social post is performed by this work.
