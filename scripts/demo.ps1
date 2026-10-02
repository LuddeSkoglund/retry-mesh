$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet test tests/RetryMesh.IntegrationTests --filter FullyQualifiedName~RealSampleChainHasExactCounts --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'The demo failed; exact request counts were not verified.' }
    Write-Host 'Without coordination: ServiceC requests: 9'
    Write-Host 'With coordination:    ServiceC requests: 3'
    Write-Host 'Retry amplification prevented: 6 requests'
    Write-Host 'Reduction: 66.7%'
} finally { Pop-Location }
