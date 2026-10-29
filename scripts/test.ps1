param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot/build.ps1" -Configuration $Configuration
dotnet test "$root/AiLocalApi.slnx" -c $Configuration --no-build --logger 'trx;LogFileName=tests.trx' --results-directory "$root/artifacts/test-results"
if ($LASTEXITCODE) { throw 'Tests failed' }
& "$PSScriptRoot/test-publish-merge.ps1"
