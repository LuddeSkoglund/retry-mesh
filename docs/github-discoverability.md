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
| Published NuGet version, including previews | `https://img.shields.io/nuget/vpre/RetryMesh.Http` | HTTP 200 SVG; `v0.1.0-preview.3` |
| Main branch CI | `https://github.com/LuddeSkoglund/retry-mesh/actions/workflows/ci.yml/badge.svg?branch=main` | HTTP 200 SVG; passing |
| MIT license | `https://img.shields.io/badge/license-MIT-blue.svg` | HTTP 200 SVG; MIT |

The dynamic version badge shows the published feed, not the unuploaded stable artifact. NuGet's
version index contained preview.1, preview.2 and preview.3; **0.1.0 was not published** at this check.
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
