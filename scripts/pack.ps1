param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ReleaseNotes,
    [string]$MakeNsis = 'C:\Program Files (x86)\NSIS\makensis.exe',
    [string]$SignTemplate,
    [string]$SignInstallerScript
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((& $MakeNsis /VERSION) -ne 'v3.12') { throw 'NSIS 3.12 required' }
dotnet tool restore
if ($LASTEXITCODE) { throw 'Tool restore failed' }
$arguments = @('pack','--packId','AiLocalApi','--packVersion',$Version,'--packDir',"$PublishDirectory/app",'--outputDir',$OutputDirectory,'--channel','win','--runtime','win-x64','--mainExe','AiLocalApi.Desktop.exe','--packTitle','AiLocalApi','--packAuthors','silevilence','--icon',"$root/src/AiLocalApi.Desktop/app.ico",'--shortcuts','StartMenuRoot')
if ($ReleaseNotes) { $arguments += @('--releaseNotes',[IO.Path]::GetFullPath($ReleaseNotes)) }
if ($SignTemplate) { $arguments += @('--signTemplate',$SignTemplate) }
dotnet vpk @arguments
if ($LASTEXITCODE) { throw 'Velopack packaging failed' }
$payload = Join-Path $OutputDirectory 'AiLocalApi-win-Setup.exe'
if (!(Test-Path -LiteralPath $payload)) { throw "Missing Velopack payload: $payload" }
$installer = Join-Path $OutputDirectory "AiLocalApi-$Version-win-x64-Setup.exe"
& $MakeNsis /INPUTCHARSET UTF8 "/DVERSION=$Version" "/DPAYLOAD=$payload" "/DOUTPUT=$installer" "$root/packaging/installer.nsi"
if ($LASTEXITCODE) { throw 'NSIS packaging failed' }
if ($SignInstallerScript) { & $SignInstallerScript -FilePath $installer; if (!$?) { throw 'Installer signing failed' } }
# Only these files are user-facing Release assets. Raw Setup stays a local intermediate.
$assets = @($installer, (Join-Path $OutputDirectory 'AiLocalApi-win-Portable.zip'), (Join-Path $OutputDirectory "AiLocalApi-$Version-full.nupkg"), (Join-Path $OutputDirectory 'releases.win.json'))
foreach ($asset in $assets) { if (!(Test-Path -LiteralPath $asset)) { throw "Missing release asset: $asset" } }
$assets | ForEach-Object { @{ name = [IO.Path]::GetFileName($_); sha256 = (Get-FileHash -LiteralPath $_).Hash; bytes = (Get-Item -LiteralPath $_).Length } } | ConvertTo-Json | Set-Content "$OutputDirectory/assets.json" -Encoding utf8
& "$PSScriptRoot/check-assets.ps1" -Version $Version -Directory $OutputDirectory
