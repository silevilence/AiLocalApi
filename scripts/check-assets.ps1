param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory)
$names = @("AiLocalApi-$Version-win-x64-Setup.exe", 'AiLocalApi-win-Portable.zip', "AiLocalApi-$Version-full.nupkg", 'releases.win.json')
foreach ($name in $names) { if (!(Test-Path -LiteralPath "$Directory/$name")) { throw "Missing asset: $name" } }
$manifest = Get-Content -LiteralPath "$Directory/releases.win.json" -Raw | ConvertFrom-Json
$full = @($manifest.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $Version })
if ($full.Count -ne 1 -or $full[0].PackageId -ne 'AiLocalApi' -or $full[0].FileName -ne $names[2]) { throw 'Invalid update manifest identity/version' }
if ($full[0].SHA256 -ne (Get-FileHash -LiteralPath "$Directory/$($names[2])").Hash -or $full[0].Size -ne (Get-Item -LiteralPath "$Directory/$($names[2])").Length) { throw 'Update package checksum/size mismatch' }
$outerVersion = (Get-Item -LiteralPath "$Directory/$($names[0])").VersionInfo.ProductVersion
if ($outerVersion -ne $Version) { throw "NSIS version mismatch: $outerVersion" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead("$Directory/$($names[1])")
try {
    foreach ($name in '.portable', 'AiLocalApi.exe', 'Update.exe', 'current/sq.version', 'current/AiLocalApi.Desktop.exe', 'current/worker/AiLocalApi.Worker.exe', 'current/release.json') {
        if (!$zip.GetEntry($name)) { throw "Missing portable layout entry: $name" }
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('current/sq.version').Open())
    try { $spec = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($spec.package.metadata.version -ne $Version) { throw 'Portable version mismatch' }
} finally { $zip.Dispose() }
Write-Output "PASS: four Release assets, update digest, NSIS/portable version $Version and portable metadata"
