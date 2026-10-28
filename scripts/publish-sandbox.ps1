param([string]$VcRedist = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Redist\MSVC\14.44.35112\x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
foreach ($item in @(@('src/AiLocalApi.Desktop','app'), @('src/AiLocalApi.Worker','app/worker'), @('tools/AiLocalApi.Verify','verify'))) {
    dotnet publish "$root/$($item[0])" -c Release -r win-x64 --self-contained true -o "$root/artifacts/sandbox/$($item[1])"
    if ($LASTEXITCODE) { throw "Publish failed: $($item[0])" }
}
New-Item -ItemType Directory -Path "$root/artifacts/sandbox/app/worker/ffmpeg" -Force | Out-Null
$native = Get-ChildItem "$root/artifacts/native" -Filter 'avcodec-*.dll' -Recurse | Select-Object -First 1
if (!$native) { throw 'Run setup-native.ps1 first' }
Copy-Item "$($native.DirectoryName)/*.dll" "$root/artifacts/sandbox/app/worker/ffmpeg" -Force
Copy-Item "$($native.Directory.Parent.FullName)/LICENSE.txt" "$root/artifacts/sandbox/app/worker/ffmpeg/LICENSE.txt" -Force
Copy-Item "$root/docs/THIRD-PARTY.md" "$root/artifacts/sandbox/app/THIRD-PARTY.md" -Force
foreach ($group in 'Microsoft.VC143.CRT', 'Microsoft.VC143.OPENMP') {
    if (!(Test-Path "$VcRedist/$group")) { throw "Provide -VcRedist pointing to redistributable MSVC 14.44.35112 x64 files: $VcRedist/$group" }
    foreach ($library in Get-ChildItem "$VcRedist/$group" -Filter '*.dll') {
        $target = Join-Path "$root/artifacts/sandbox/app/worker" $library.Name
        if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $library.FullName).Hash) { continue }
        Copy-Item -LiteralPath $library.FullName -Destination $target -ErrorAction Stop
    }
}
Copy-Item "$root/artifacts/fixtures" "$root/artifacts/sandbox" -Recurse -Force
Copy-Item "$root/artifacts/models" "$root/artifacts/sandbox" -Recurse -Force
