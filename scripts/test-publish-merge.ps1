$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'))
$fixture = Join-Path $artifactRoot ('publish-merge-test-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture 'worker'
$destination = Join-Path $fixture 'app'
New-Item -ItemType Directory -Path $source, $destination | Out-Null
try {
    foreach ($path in 'System.Private.CoreLib.dll', 'AiLocalApi.Worker.exe', 'runtimes/win-x64/whisper.dll', 'runtimes/vulkan/win-x64/whisper.dll', 'runtimes/vulkan/linux-x64/libwhisper.so', 'runtimes/win-arm64/whisper.dll', 'runtimes/win-x86/whisper.dll') {
        $file = Join-Path $source $path
        New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
        'fixture' | Set-Content -LiteralPath $file
    }
    Copy-Item -LiteralPath "$source/System.Private.CoreLib.dll" -Destination $destination
    & "$PSScriptRoot/merge-worker-publish.ps1" -Source $source -Destination $destination
    foreach ($path in 'System.Private.CoreLib.dll', 'AiLocalApi.Worker.exe', 'runtimes/win-x64/whisper.dll', 'runtimes/vulkan/win-x64/whisper.dll') {
        if (!(Test-Path -LiteralPath (Join-Path $destination $path))) { throw "Missing retained file: $path" }
    }
    if (@(Get-ChildItem -LiteralPath $destination -File -Recurse).Count -ne 4) { throw 'Unexpected duplicate or foreign architecture files.' }
    'conflicting runtime' | Set-Content -LiteralPath "$source/System.Private.CoreLib.dll"
    'must not copy' | Set-Content -LiteralPath "$source/new-file.dll"
    $rejected = $false
    try { & "$PSScriptRoot/merge-worker-publish.ps1" -Source $source -Destination $destination } catch {
        if ($_.Exception.Message -notlike 'Conflicting shared publish file:*') { throw }
        $rejected = $true
    }
    if (!$rejected -or (Get-Content -LiteralPath "$destination/System.Private.CoreLib.dll") -ne 'fixture' -or (Test-Path -LiteralPath "$destination/new-file.dll")) { throw 'Conflict must fail before changing the destination.' }
    $rejected = $false
    try { & "$PSScriptRoot/merge-worker-publish.ps1" -Source $source -Destination $source } catch { $rejected = $true }
    if (!$rejected) { throw 'Overlapping directories must be rejected.' }
    Write-Output 'PASS: shared runtime deduplication, x64 CPU/Vulkan selection, conflict rejection before copying and directory isolation'
}
finally {
    $resolved = (Resolve-Path -LiteralPath $fixture).Path
    if (!$resolved.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test cleanup escaped artifacts.' }
    Remove-Item -LiteralPath $resolved -Recurse -ErrorAction Stop
}
