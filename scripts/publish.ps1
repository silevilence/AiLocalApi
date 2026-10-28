param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutputDirectory,
    [string]$UpdateSource = 'https://github.com/silevilence/AiLocalApi',
    [string]$VcRedist
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = "$root/artifacts/publish/$Version" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Output already exists; select a new empty output directory: $OutputDirectory" }
if ((dotnet --version) -ne '10.0.301') { throw 'SDK 10.0.301 required' }
if (!$VcRedist) { $VcRedist = & "$PSScriptRoot/setup-redist.ps1" }
foreach ($group in 'Microsoft.VC143.CRT', 'Microsoft.VC143.OPENMP') {
    if (!(Test-Path -LiteralPath "$VcRedist/$group")) { throw "Missing redistributable: $VcRedist/$group" }
}
& "$PSScriptRoot/setup-native.ps1"
dotnet restore "$root/AiLocalApi.slnx" --locked-mode
if ($LASTEXITCODE) { throw 'Locked restore failed' }
foreach ($item in @(@('src/AiLocalApi.Desktop','app'), @('src/AiLocalApi.Worker','app/worker'), @('tools/AiLocalApi.Verify','verify'))) {
    dotnet publish "$root/$($item[0])" -c Release -r win-x64 --self-contained true --no-restore -p:Version=$Version -o "$OutputDirectory/$($item[1])"
    if ($LASTEXITCODE) { throw "Publish failed: $($item[0])" }
}
$ffmpeg = "$OutputDirectory/app/worker/ffmpeg"
New-Item -ItemType Directory -Path $ffmpeg | Out-Null
$native = Get-ChildItem "$root/artifacts/native" -Filter 'avcodec-*.dll' -Recurse | Select-Object -First 1
Copy-Item "$($native.DirectoryName)/*.dll" $ffmpeg
Copy-Item "$($native.Directory.Parent.FullName)/LICENSE.txt" "$ffmpeg/LICENSE.txt"
Copy-Item "$root/docs/THIRD-PARTY.md" "$OutputDirectory/app/THIRD-PARTY.md"
foreach ($group in 'Microsoft.VC143.CRT', 'Microsoft.VC143.OPENMP') {
    Copy-Item "$VcRedist/$group/*.dll" "$OutputDirectory/app/worker"
}
@{ repository = $UpdateSource; channel = 'win'; packageId = 'AiLocalApi' } | ConvertTo-Json | Set-Content "$OutputDirectory/app/release.json" -Encoding utf8
Write-Output "Published self-contained $Version to $OutputDirectory"
