<#
  Teste le client Bluetooth du plugin sans lancer SimHub.
    .\run.ps1 [secondes] [adresse]
  Compile d'abord le plugin (plugin\build.ps1).
#>
param([int]$Seconds = 40, [string]$Address = "")
$ErrorActionPreference = "Stop"
$here   = $PSScriptRoot
$out    = Join-Path $here "bin"
$simhub = if ($env:SIMHUB_INSTALL_PATH) { $env:SIMHUB_INSTALL_PATH } else { "F:\Program Files (x86)\SimHub\" }
$fw     = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$wm     = "C:\Windows\System32\WinMetadata"
$dll    = Join-Path $here "..\..\plugin\bin\Release\User.DafitWatch.dll"

New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item $dll $out -Force
Copy-Item (Join-Path $simhub "SimHub.Logging.dll"), (Join-Path $simhub "log4net.dll") $out -Force

& "$fw\csc.exe" /nologo /codepage:65001 /out:"$out\WatchClientTest.exe" `
    /r:"$out\User.DafitWatch.dll" /r:"$wm\Windows.Devices.winmd" /r:"$wm\Windows.Foundation.winmd" `
    /r:"$fw\System.Runtime.dll" "$here\Program.cs"
if ($LASTEXITCODE -ne 0) { exit 1 }

[Console]::OutputEncoding = [Text.Encoding]::UTF8
& "$out\WatchClientTest.exe" $Seconds $Address
