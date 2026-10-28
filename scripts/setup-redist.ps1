$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $root 'artifacts/redist'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
# Official VS package catalog: 14.44.35211 binaries in the 14.44.35112 layout.
$hash = '4AAF54DB0BFC9435F7C3660E1A00237A4B556042BFEEA64BDE44C2E0194E6EE5'
$url = 'https://download.visualstudio.microsoft.com/download/pr/45d3b8dd-bced-4b37-9974-142f748d710c/4aaf54db0bfc9435f7c3660e1a00237a4b556042bfeea64bde44c2e0194e6ee5/Microsoft.VC.14.44.17.14.CRT.Redist.X64.base.vsix'
$archive = Join-Path $cache 'crt-14.44.35211.vsix'
if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest $url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive).Hash -ne $hash) { throw 'MSVC package checksum mismatch' }
$target = Join-Path $cache 'x64'
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($group in 'Microsoft.VC143.CRT', 'Microsoft.VC143.OpenMP') {
        New-Item -ItemType Directory -Path "$target/$group" -Force | Out-Null
        $prefix = "Contents/VC/Redist/MSVC/14.44.35112/x64/$group/"
        foreach ($entry in $zip.Entries | Where-Object { $_.FullName.StartsWith($prefix) -and $_.Name.EndsWith('.dll') }) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, "$target/$group/$($entry.Name)", $true)
        }
        if (!(Get-ChildItem "$target/$group" -Filter *.dll)) { throw "Missing runtime files: $group" }
    }
} finally { $zip.Dispose() }
Write-Output $target
