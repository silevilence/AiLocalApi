$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('AiLocalApi-release-notes-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$changelog = "# Change Log`n`n## V0.1.1`n`n### New`n`n- Second version`n`n## V0.1.0`n`n### New`n`n- First version`n"
function Check-Rejected([string]$label, [scriptblock]$operation) {
    $rejected = $false
    try { & $operation | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw "Expected rejection: $label" }
    Write-Output "PASS: $label"
}
foreach ($version in '0.1.0', '0.1.1') {
    "<Project><PropertyGroup><Version>$version</Version></PropertyGroup></Project>" | Set-Content "$fixture/Directory.Build.props"
    $changelog | Set-Content "$fixture/changelog.md"
    $notes = & "$PSScriptRoot/release-notes.ps1" -Version $version -SourceRoot $fixture
    $expected = if ($version -eq '0.1.0') { 'First version' } else { 'Second version' }
    if ($notes -notmatch $expected -or $notes -match '^## V') { throw 'Wrong section extraction' }
    Write-Output "PASS: isolate $version notes"
}
Check-Rejected 'project version mismatch' { & "$PSScriptRoot/release-notes.ps1" -Version 0.1.0 -SourceRoot $fixture }
"# Change Log`n## V0.1.1`n### New`n" | Set-Content "$fixture/changelog.md"
Check-Rejected 'empty section' { & "$PSScriptRoot/release-notes.ps1" -Version 0.1.1 -SourceRoot $fixture }
($changelog + "`n## V0.1.1`n- Duplicate`n") | Set-Content "$fixture/changelog.md"
Check-Rejected 'duplicate section' { & "$PSScriptRoot/release-notes.ps1" -Version 0.1.1 -SourceRoot $fixture }
"# Change Log`n## V0.1.0`n- Old`n" | Set-Content "$fixture/changelog.md"
Check-Rejected 'missing section' { & "$PSScriptRoot/release-notes.ps1" -Version 0.1.1 -SourceRoot $fixture }
