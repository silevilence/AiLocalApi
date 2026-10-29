param([string]$Configuration = 'Debug', [switch]$EnableCuda)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot/setup-ctranslate2.ps1" -EnableCuda:$EnableCuda
if (!$?) { throw 'CTranslate2 native build failed' }
$ct2Variant = if ($EnableCuda) { 'cuda' } else { 'cpu' }
dotnet build "$root/AiLocalApi.slnx" -c $Configuration -p:EnableCuda=$($EnableCuda.IsPresent.ToString().ToLowerInvariant()) -p:CTranslateVariant=$ct2Variant
if ($LASTEXITCODE) { throw 'Build failed' }
$worker = "$root/src/AiLocalApi.Worker/bin/$Configuration/net10.0-windows"
$destination = "$root/src/AiLocalApi.Desktop/bin/$Configuration/net10.0-windows/worker"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item "$worker/*" $destination -Recurse -Force
$native = Get-ChildItem "$root/artifacts/native" -Filter 'avcodec-*.dll' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($native) {
    foreach ($target in @("$worker/ffmpeg", "$destination/ffmpeg")) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item "$($native.DirectoryName)/*.dll" $target -Force
        Copy-Item "$($native.Directory.Parent.FullName)/LICENSE.txt" "$target/LICENSE.txt" -Force
    }
}
