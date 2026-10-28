param([string]$DataDirectory, [switch]$Tray)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot/build.ps1"
if ($DataDirectory) { $env:AILOCAL_DATA = [IO.Path]::GetFullPath($DataDirectory) }
$arguments = if ($Tray) { @('--tray') } else { @() }
Start-Process -FilePath "$root/src/AiLocalApi.Desktop/bin/Debug/net10.0-windows/AiLocalApi.Desktop.exe" -ArgumentList ($arguments + @('--development')) -WindowStyle Hidden
