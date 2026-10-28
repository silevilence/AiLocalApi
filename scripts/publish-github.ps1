param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[\w.-]+/[\w.-]+$')][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$Commit,
    [Parameter(Mandatory)][string]$Notes,
    [string]$Directory,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
function Invoke-Gh { $result = & gh @args; if ($LASTEXITCODE) { throw "GitHub command failed ($($args[0]))" }; return $result }
$tag = "v$Version"
$names = @("AiLocalApi-$Version-win-x64-Setup.exe", 'AiLocalApi-win-Portable.zip', "AiLocalApi-$Version-full.nupkg", 'releases.win.json')
$body = (Get-Content -LiteralPath $Notes -Raw).TrimEnd()
$releases = Invoke-Gh release list --repo $Repository --limit 1000 --json tagName | ConvertFrom-Json
$release = $null
if ($releases | Where-Object tagName -eq $tag) { $release = Invoke-Gh api "repos/$Repository/releases/tags/$tag" | ConvertFrom-Json }
if ($release) {
    if ($release.target_commitish -ne $Commit -or $release.body.TrimEnd() -ne $body) { throw 'Existing release differs from this tag commit or changelog; refusing to overwrite' }
    if (!$release.draft) {
        if ($release.prerelease -or $release.assets.Count -ne 4) { throw 'Published release has unexpected state/assets' }
        foreach ($name in $names) {
            $asset = @($release.assets | Where-Object name -eq $name)
            if ($asset.Count -ne 1 -or $asset[0].size -le 0 -or $asset[0].digest -notmatch '^sha256:[0-9a-f]{64}$') { throw "Incomplete published asset: $name" }
        }
        if ($env:GITHUB_OUTPUT) { 'published=true' >> $env:GITHUB_OUTPUT }
        Write-Output "Already published from this exact commit: $tag; no assets changed"
        return
    }
}
if ($env:GITHUB_OUTPUT) { 'published=false' >> $env:GITHUB_OUTPUT }
if ($CheckOnly) { return }
if (!$Directory) { throw 'Release asset directory is required' }
& "$PSScriptRoot/check-assets.ps1" -Version $Version -Directory $Directory
if (!$release) {
    Invoke-Gh release create $tag --repo $Repository --verify-tag --target $Commit --title "V$Version" --notes-file $Notes --draft
}
# A failed upload can be retried against the same unpublished draft; published releases are immutable here.
foreach ($name in $names) { Invoke-Gh release upload $tag "$Directory/$name" --repo $Repository --clobber }
$release = Invoke-Gh api "repos/$Repository/releases/tags/$tag" | ConvertFrom-Json
if (!$release.draft -or $release.assets.Count -ne 4) { throw 'Draft state or final asset count changed unexpectedly' }
foreach ($name in $names) {
    $asset = @($release.assets | Where-Object name -eq $name)
    $local = Get-Item -LiteralPath "$Directory/$name"
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $local.FullName).Hash.ToLowerInvariant()
    if ($asset.Count -ne 1 -or $asset[0].size -ne $local.Length -or $asset[0].digest -ne $digest) { throw "Uploaded asset mismatch: $name" }
}
Invoke-Gh release edit $tag --repo $Repository --draft=false --latest
Write-Output "Published complete release $tag"
