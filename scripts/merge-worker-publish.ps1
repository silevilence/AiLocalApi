param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path
$Destination = (Resolve-Path -LiteralPath $Destination).Path
if ($Source -eq $Destination -or $Source.StartsWith($Destination + '\', [StringComparison]::OrdinalIgnoreCase) -or $Destination.StartsWith($Source + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish directories must not overlap.' }
# Both apphosts remain self-contained and resolve their own deps/runtimeconfig files.
# Share only byte-identical files; a dependency version conflict must fail the build.
$copies = @()
foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($Source, $file.FullName).Replace('\', '/')
    if ($relative.StartsWith('runtimes/') -and $relative -notmatch '^runtimes/(?:[^/]+/)?win-x64/') { continue }
    $target = Join-Path $Destination $relative
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw "Conflicting shared publish file: $relative" }
    }
    else { $copies += @{ Source = $file.FullName; Target = $target } }
}
foreach ($copy in $copies) {
    New-Item -ItemType Directory -Path (Split-Path $copy.Target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $copy.Source -Destination $copy.Target
}
