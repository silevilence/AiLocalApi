param([switch]$EnableCuda, [int]$Parallel = 6)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = "$root/artifacts/ctranslate2/source"
$variant = if ($EnableCuda) { 'cuda' } else { 'cpu' }
$build = "$root/artifacts/ctranslate2/build-$variant"
$bridge = "$root/artifacts/ctranslate2/bridge-$variant"
$output = "$root/artifacts/ctranslate2/runtime-$variant"
$revision = '617405f4b050e994e829d527da6caa0e0030afe7' # CTranslate2 4.6.0
$vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$vs = & $vswhere -version '[17.0,18.0)' -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio 2022 C++ tools are required to build the native runtime.' }
$cmake = "$vs/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"
if (!(Test-Path -LiteralPath $cmake)) { throw 'Install the Visual Studio C++ CMake tools component.' }
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE) { throw "$Program exited with $LASTEXITCODE" }
}
if (!(Test-Path -LiteralPath "$source/.git")) {
    Invoke-Checked git @('clone', '--branch', 'v4.6.0', '--depth', '1', 'https://github.com/OpenNMT/CTranslate2.git', $source)
}
if ((& git -C $source rev-parse HEAD) -ne $revision) { throw 'Unexpected CTranslate2 source revision; use a separate source directory.' }
if (& git -C $source status --porcelain --untracked-files=no) { throw 'CTranslate2 source has local changes.' }
Invoke-Checked git @('-C', $source, 'submodule', 'update', '--init', '--recursive', '--depth', '1', 'third_party/cpu_features', 'third_party/ruy', 'third_party/spdlog')
if ($EnableCuda) {
    Invoke-Checked git @('-C', $source, 'submodule', 'update', '--init', '--recursive', '--depth', '1', 'third_party/thrust', 'third_party/cutlass')
}
$cudaFlag = if ($EnableCuda) { 'ON' } else { 'OFF' }
Invoke-Checked $cmake @('-S', $source, '-B', $build, '-G', 'Visual Studio 17 2022', '-A', 'x64', '-DWITH_MKL=OFF', '-DWITH_RUY=ON', '-DOPENMP_RUNTIME=COMP', '-DBUILD_CLI=OFF', '-DBUILD_TESTS=OFF', '-DBUILD_SHARED_LIBS=ON', '-DCMAKE_CXX_FLAGS=/DWIN32 /D_WINDOWS /W3 /GR /EHsc /utf-8', "-DWITH_CUDA=$cudaFlag", "-DWITH_CUDNN=$cudaFlag")
Invoke-Checked $cmake @('--build', $build, '--config', 'Release', '--parallel', "$Parallel")
Invoke-Checked $cmake @('-S', "$root/native/ctranslate2", '-B', $bridge, '-G', 'Visual Studio 17 2022', '-A', 'x64', "-DCT2_SOURCE=$source", "-DCT2_BUILD=$build")
Invoke-Checked $cmake @('--build', $bridge, '--config', 'Release', '--parallel', "$Parallel")
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath "$build/Release/ctranslate2.dll", "$bridge/Release/ailocal_ct2.dll" -Destination $output
Copy-Item -LiteralPath "$source/LICENSE" -Destination "$output/CTranslate2-LICENSE.txt"
foreach ($dependency in @('ruy', 'spdlog', 'cpu_features')) {
    $license = Get-ChildItem "$source/third_party/$dependency" -Filter 'LICENSE*' | Select-Object -First 1
    if ($license) { Copy-Item -LiteralPath $license.FullName -Destination "$output/$dependency-LICENSE.txt" }
}
Copy-Item -LiteralPath "$source/third_party/ruy/third_party/cpuinfo/LICENSE" -Destination "$output/cpuinfo-LICENSE.txt"
if ($EnableCuda) {
    Copy-Item -LiteralPath "$source/third_party/thrust/LICENSE" -Destination "$output/thrust-LICENSE.txt"
    Copy-Item -LiteralPath "$source/third_party/thrust/dependencies/cub/LICENSE.TXT" -Destination "$output/cub-LICENSE.txt"
    Copy-Item -LiteralPath "$source/third_party/cutlass/LICENSE.txt" -Destination "$output/cutlass-LICENSE.txt"
}
foreach ($header in @('avx_mathfun.h', 'avx512_mathfun.h', 'neon_mathfun.h', 'BS_thread_pool_light.hpp')) {
    Copy-Item -LiteralPath "$source/third_party/$header" -Destination "$output/$header-LICENSE.txt"
}
Get-ChildItem $output -Filter '*.dll' | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash | ConvertTo-Json | Set-Content "$output/sha256.json" -Encoding utf8
Write-Output "CTranslate2 4.6.0 ($variant): $output"
