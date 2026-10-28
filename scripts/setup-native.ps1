$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $root 'artifacts/native'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive = Join-Path $cache 'ffmpeg-9.0.zip'
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-27-18-38/ffmpeg-n9.0.2-36-g6882969ce9-win64-lgpl-shared-9.0.zip'
$hash = 'F70E43C4F5670FBBF2DC4EB667B0D6B3743D68386F766C08BB2E0EC3EDECE398'
if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest $url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $hash) { throw "FFmpeg checksum mismatch: $archive" }
Expand-Archive -LiteralPath $archive -DestinationPath $cache -Force
Write-Output 'FFmpeg shared libraries verified. Run build.ps1 to copy the DLLs.'
