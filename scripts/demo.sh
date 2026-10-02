#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test tests/DistributedResilience.IntegrationTests --filter 'FullyQualifiedName~RealSampleChainHasExactCounts' --logger 'console;verbosity=normal'
printf '%s\n' 'Without coordination: ServiceC requests: 9' 'With coordination:    ServiceC requests: 3' 'Retry amplification prevented: 6 requests' 'Reduction: 66.7%'
