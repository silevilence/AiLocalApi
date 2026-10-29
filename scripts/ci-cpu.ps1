param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixtures = "$root/artifacts/ci-fixtures"
New-Item -ItemType Directory -Path $fixtures -Force | Out-Null
$audio = "$fixtures/jfk.wav"
Invoke-WebRequest 'https://raw.githubusercontent.com/ggml-org/whisper.cpp/b0a11594aec50892a02cd8d129eee2dfe93a8bb8/samples/jfk.wav' -OutFile $audio
if ((Get-FileHash -LiteralPath $audio).Hash -ne '59DFB9A4ACB36FE2A2AFFC14BACBEE2920FF435CB13CC314A08C13F66BA7860E') { throw 'Audio checksum mismatch' }
$env:AILOCAL_WORKER = [IO.Path]::GetFullPath("$PublishDirectory/app/worker/AiLocalApi.Worker.exe")
& "$PublishDirectory/verify/AiLocalApi.Verify.exe" "$root/artifacts/ci-data" "$root/artifacts/ci-models" $audio cpu --download --report "$root/artifacts/ci-cpu.txt"
if ($LASTEXITCODE) { throw 'Published CPU inference/reuse/unload failed; see ci-cpu.txt' }
Get-Content "$root/artifacts/ci-cpu.txt"
& "$PublishDirectory/verify/AiLocalApi.Verify.exe" --engine "$root/artifacts/ci-ct2-data" "$root/artifacts/ci-models" $audio ct2-tiny --report "$root/artifacts/ci-ct2.txt"
if ($LASTEXITCODE) { throw 'Published CTranslate2 CPU inference failed; see ci-ct2.txt' }
Get-Content "$root/artifacts/ci-ct2.txt"
