$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixtures = Join-Path $root 'artifacts/fixtures'
New-Item -ItemType Directory -Path $fixtures -Force | Out-Null
$wave = Join-Path $fixtures 'jfk.wav'
if (!(Test-Path -LiteralPath $wave)) { Invoke-WebRequest 'https://raw.githubusercontent.com/ggml-org/whisper.cpp/b0a11594aec50892a02cd8d129eee2dfe93a8bb8/samples/jfk.wav' -OutFile $wave }
if ((Get-FileHash -LiteralPath $wave).Hash -ne '59DFB9A4ACB36FE2A2AFFC14BACBEE2920FF435CB13CC314A08C13F66BA7860E') { throw 'JFK fixture checksum mismatch' }
$voice = New-Object -ComObject SAPI.SpVoice
$voices = $voice.GetVoices('Language=804')
if ($voices.Count -eq 0) { throw 'A Chinese SAPI voice is required to generate the Chinese test fixture' }
$stream = New-Object -ComObject SAPI.SpFileStream
try { $stream.Open((Join-Path $fixtures 'chinese.wav'),3); $voice.Voice = $voices.Item(0); $voice.AudioOutputStream = $stream; $voice.Speak('你好，这是本地语音识别测试。今天的天气很好。') | Out-Null }
finally { $stream.Close() }
dotnet run --project "$root/tools/AiLocalApi.Verify" -- --fixtures $wave "$root/src/AiLocalApi.Desktop/bin/Debug/net10.0-windows/worker/ffmpeg"
if ($LASTEXITCODE) { throw 'Fixture encoding failed' }
