param(
    [Parameter(Mandatory)][ValidatePattern('^[vV]?\d+\.\d+\.\d+$')][string]$Version,
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$plain = $Version -replace '^[vV]', ''
$props = [xml](Get-Content -LiteralPath "$SourceRoot/Directory.Build.props" -Raw)
$declared = @($props.Project.PropertyGroup.Version | Where-Object { $_ })
if ($declared.Count -ne 1 -or $declared[0] -ne $plain) { throw "Project version does not match $plain" }
$changelog = Get-Content -LiteralPath "$SourceRoot/changelog.md" -Raw
$chapters = [regex]::Matches($changelog, '(?ms)^## [vV]([^\r\n]+)\r?\n(.*?)(?=^## |\z)')
$matching = @($chapters | Where-Object { $_.Groups[1].Value.Trim() -eq $plain })
if ($matching.Count -ne 1) { throw "Expected exactly one changelog section for $plain" }
$notes = $matching[0].Groups[2].Value.Trim()
if ($notes -notmatch '(?m)^- \S') { throw "Empty release notes for $plain" }
if ($OutputFile) { $notes | Set-Content -LiteralPath $OutputFile -Encoding utf8 }
else { Write-Output $notes }
