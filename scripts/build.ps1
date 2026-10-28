param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
dotnet build "$root/AiLocalApi.slnx" -c $Configuration
if ($LASTEXITCODE) { throw 'Build failed' }
$worker = "$root/src/AiLocalApi.Worker/bin/$Configuration/net10.0-windows"
$destination = "$root/src/AiLocalApi.Desktop/bin/$Configuration/net10.0-windows/worker"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item "$worker/*" $destination -Recurse -Force
