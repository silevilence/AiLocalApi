param([string]$VcRedist)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$props = [xml](Get-Content -LiteralPath "$root/Directory.Build.props" -Raw)
& "$PSScriptRoot/publish.ps1" -Version $props.Project.PropertyGroup.Version -OutputDirectory "$root/artifacts/sandbox" -VcRedist $VcRedist
Copy-Item -LiteralPath "$root/artifacts/fixtures" -Destination "$root/artifacts/sandbox" -Recurse
Copy-Item -LiteralPath "$root/artifacts/models" -Destination "$root/artifacts/sandbox" -Recurse
