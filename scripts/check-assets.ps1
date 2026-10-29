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
    foreach ($name in '.portable', 'AiLocalApi.exe', 'Update.exe', 'current/sq.version', 'current/AiLocalApi.Desktop.exe', 'current/AiLocalApi.Worker.exe', 'current/release.json', 'current/AiLocalApi.Worker.deps.json', 'current/AiLocalApi.Worker.runtimeconfig.json', 'current/ctranslate2.dll', 'current/ailocal_ct2.dll', 'current/runtimes/win-x64/whisper.dll', 'current/runtimes/vulkan/win-x64/ggml-vulkan-whisper.dll', 'current/ffmpeg/avcodec-63.dll', 'current/ffmpeg/avformat-63.dll', 'current/ffmpeg/avutil-61.dll', 'current/ffmpeg/swresample-7.dll', 'current/ffmpeg/LICENSE.txt') {
        if (!$zip.GetEntry($name)) { throw "Missing portable layout entry: $name" }
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('current/sq.version').Open())
    try { $spec = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($spec.package.metadata.version -ne $Version) { throw 'Portable version mismatch' }
    if (@($zip.Entries | Where-Object { $_.Name -eq 'System.Private.CoreLib.dll' }).Count -ne 1) { throw 'Expected exactly one shared .NET runtime.' }
    foreach ($entry in $zip.Entries) {
        if (!$entry.Name) { continue }
        if ($entry.FullName.StartsWith('current/worker/')) { throw 'Legacy duplicated Worker directory in release.' }
        if ($entry.FullName.StartsWith('current/runtimes/') -and $entry.FullName -notmatch '^current/runtimes/(?:[^/]+/)?win-x64/') { throw "Unexpected runtime architecture: $($entry.FullName)" }
        if ($entry.FullName -match '^current/ffmpeg/.*\.dll$' -and $entry.Name -notin @('avcodec-63.dll', 'avformat-63.dll', 'avutil-61.dll', 'swresample-7.dll')) { throw "Unused FFmpeg library: $($entry.Name)" }
    }
    foreach ($assembly in 'current/AiLocalApi.Desktop.dll', 'current/AiLocalApi.Worker.dll') {
        $entry = $zip.GetEntry($assembly)
        if (!$entry) { throw "Missing assembly: $assembly" }
        $stream = $entry.Open(); $buffer = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($buffer); $buffer.Position = 0
            $pe = [Reflection.PortableExecutable.PEReader]::new($buffer)
            try {
                $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
                if ($metadata.GetAssemblyDefinition().Version.ToString() -ne "$Version.0") { throw "Application version mismatch: $assembly" }
            } finally { $pe.Dispose() }
        } finally { $stream.Dispose(); $buffer.Dispose() }
    }
} finally { $zip.Dispose() }
Write-Output "PASS: four Release assets, update digest, NSIS/portable/host/Worker version $Version and portable metadata"
