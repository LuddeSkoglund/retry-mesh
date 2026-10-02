param([switch]$SkipPack)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$packageDirectory = Join-Path $repositoryRoot 'artifacts/packages'
$consumerRoot = Join-Path $repositoryRoot ('artifacts/package-consumers/' + [Guid]::NewGuid().ToString('N'))
[xml]$project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/RetryMesh.Http/RetryMesh.Http.csproj')
$version = [string]$project.Project.PropertyGroup.Version
New-Item -ItemType Directory -Force -Path $consumerRoot | Out-Null
$configPath = Join-Path $consumerRoot 'NuGet.Config'
@"
<configuration><packageSources><clear /><add key="packed" value="$([System.Security.SecurityElement]::Escape($packageDirectory))" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources><packageSourceMapping><packageSource key="packed"><package pattern="RetryMesh.Http" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping></configuration>
"@ | Set-Content -LiteralPath $configPath -Encoding utf8
if (!$SkipPack) {
    dotnet pack (Join-Path $repositoryRoot 'RetryMesh.sln') -c Release -o $packageDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Pack failed' }
}
$packagePath = Join-Path $packageDirectory "RetryMesh.Http.$version.nupkg"
$zip = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $names = @($zip.Entries | ForEach-Object FullName)
    foreach ($required in @('lib/net8.0/RetryMesh.Http.dll', 'lib/net10.0/RetryMesh.Http.dll', 'README.md', 'LICENSE', 'RetryMesh.Http.nuspec')) {
        if ($names -notcontains $required) { throw "Missing package entry: $required" }
    }
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -notmatch '^(_rels/\.rels|RetryMesh\.Http\.nuspec|lib/(net8\.0|net10\.0)/RetryMesh\.Http\.dll|README\.md|LICENSE|\[Content_Types\]\.xml|package/services/metadata/core-properties/[^/]+\.psmdcp)$') {
            throw "Unexpected packaged file: $($entry.FullName)"
        }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { $content = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($content -match 'DistributedResilience|C:\\Users\\|C:\\dev\\retry-mesh') { throw "Old identity or local machine path in $($entry.FullName)" }
    }
    $reader = [System.IO.StreamReader]::new($zip.GetEntry('RetryMesh.Http.nuspec').Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($manifest.package.metadata.version -ne $version) { throw 'Package version mismatch' }
    if ($manifest.package.metadata.license.type -ne 'expression' -or $manifest.package.metadata.license.InnerText -ne 'MIT') { throw 'MIT license metadata missing' }
    if ($manifest.package.metadata.repository.url -ne 'https://github.com/LuddeSkoglund/retry-mesh') { throw 'Repository metadata missing' }
    if ($manifest.package.metadata.repository.type -ne 'git' -or !$manifest.package.metadata.repository.commit) { throw 'Git repository metadata missing' }
    if (@($manifest.package.metadata.dependencies.group).Count -ne 2 -or @($manifest.package.metadata.frameworkReferences.group).Count -ne 2) { throw 'Missing framework dependency groups' }
    foreach ($group in $manifest.package.metadata.dependencies.group) {
        if ($group.targetFramework -notin @('net8.0', 'net10.0')) { throw 'Unexpected dependency TFM' }
        if ($group.dependency.id -ne 'Microsoft.Extensions.Http.Resilience' -or $group.dependency.version -ne '10.0.0') { throw 'Unexpected dependency' }
    }
    foreach ($group in $manifest.package.metadata.frameworkReferences.group) {
        if ($group.targetFramework -notin @('net8.0', 'net10.0') -or $group.frameworkReference.name -ne 'Microsoft.AspNetCore.App') { throw 'Unexpected ASP.NET framework requirement' }
    }
} finally { $zip.Dispose() }

$source = @'
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http.Resilience;
using RetryMesh;
using System.Text.Json;

static WebApplicationBuilder NewBuilder()
{
    var b = WebApplication.CreateBuilder();
    b.WebHost.UseUrls("http://127.0.0.1:0");
    b.Logging.ClearProviders();
    return b;
}
var cCount = 0;
var bCount = 0;
var aCount = 0;
var bBuilder = NewBuilder();
var cBuilder = NewBuilder();
await using var c = cBuilder.Build();
c.MapGet("/", () => { Interlocked.Increment(ref cCount); return Results.StatusCode(500); });
await c.StartAsync();
bBuilder.Services.AddRetryMesh();
bBuilder.Services.AddHttpClient("external", client => client.BaseAddress = new Uri(c.Urls.Single()))
    .AddStandardResilienceHandler(options => { options.Retry.MaxRetryAttempts = 2; options.Retry.Delay = TimeSpan.Zero; })
    .UseRetryMesh();
await using var b = bBuilder.Build();
b.UseRetryMesh();
b.MapGet("/", async (IHttpClientFactory clients) =>
{
    Interlocked.Increment(ref bCount);
    using var response = await clients.CreateClient("external").GetAsync("/");
    return Results.StatusCode((int)response.StatusCode);
});
await b.StartAsync();
var aBuilder = NewBuilder();
aBuilder.Services.AddHttpClient<InternalClient>(client => client.BaseAddress = new Uri(b.Urls.Single()))
    .AddStandardResilienceHandler(options => { options.Retry.MaxRetryAttempts = 2; options.Retry.Delay = TimeSpan.Zero; })
    .UseRetryMesh(options => options.TrustDownstreamMetadata = true);
await using var a = aBuilder.Build();
a.UseRetryMesh();
a.MapGet("/", async (InternalClient client) =>
{
    Interlocked.Increment(ref aCount);
    using var response = await client.GetAsync();
    return Results.StatusCode((int)response.StatusCode);
});
await a.StartAsync();
using var caller = new HttpClient();
using var result = await caller.GetAsync(a.Urls.Single());
var failure = RetryMeshHeaders.Read(result);
if ((int)result.StatusCode != 500 || aCount != 1 || bCount != 1 || cCount != 3 || failure?.Attempts != 3)
    throw new Exception($"Packed consumer failed: A={aCount} B={bCount} C={cCount}");
if (failure.RetriedBy != bBuilder.Environment.ApplicationName) throw new Exception("Inferred identity mismatch");
Console.WriteLine($"PACKED CONSUMER PASS: A={aCount} B={bCount} C={cCount}; {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var assembly = typeof(RetryMeshOptions).Assembly;
var api = assembly.GetExportedTypes().OrderBy(t => t.FullName).Select(t => new
{
    Type = t.FullName,
    Members = t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
        .Where(m => m is System.Reflection.MethodInfo or System.Reflection.PropertyInfo or System.Reflection.FieldInfo or System.Reflection.ConstructorInfo)
        .Select(m => m.ToString()).Order().ToArray()
});
File.WriteAllText("public-api.json", JsonSerializer.Serialize(api, new JsonSerializerOptions { WriteIndented = true }));

public sealed class InternalClient(HttpClient client)
{
    public Task<HttpResponseMessage> GetAsync() => client.GetAsync("/");
}
'@
foreach ($framework in @('net8.0', 'net10.0')) {
    $directory = Join-Path $consumerRoot $framework
    New-Item -ItemType Directory -Path $directory | Out-Null
    $projectPath = Join-Path $directory 'PackedConsumer.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>$framework</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup><PackageReference Include="RetryMesh.Http" Version="$version" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $projectPath -Encoding utf8
    $source | Set-Content -LiteralPath (Join-Path $directory 'Program.cs') -Encoding utf8
    # A fresh package cache forces consumption of THIS nupkg, even when its preview version was previously installed.
    dotnet restore $projectPath --configfile $configPath --packages (Join-Path $consumerRoot 'nuget-cache')
    if ($LASTEXITCODE -ne 0) { throw "$framework restore failed" }
    $consumedPackage = Join-Path $consumerRoot "nuget-cache/retrymesh.http/$version/retrymesh.http.$version.nupkg"
    if ((Get-FileHash -LiteralPath $consumedPackage -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash) { throw "$framework did not consume the exact local nupkg" }
    dotnet build $projectPath -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "$framework consumer build failed" }
    Push-Location $directory
    try {
        dotnet run --project $projectPath -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "$framework consumer run failed" }
    } finally { Pop-Location }
}
@{ Package = $packagePath; Version = $version; Consumers = @('net8.0', 'net10.0'); Result = 'PASS'; Directory = $consumerRoot } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repositoryRoot 'artifacts/package-release-gate.json') -Encoding utf8
Write-Host "Package release gates passed: $packagePath"
