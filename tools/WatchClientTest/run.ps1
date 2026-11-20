<#
  Teste le client Bluetooth du plugin sans lancer SimHub.
    .\run.ps1 run 60 AA:BB:CC:DD:EE:FF [profil]   connexion + cardio pendant 60 s
    .\run.ps1 list                                 appareils appairés + à proximité
    .\run.ps1 diag AA:BB:CC:DD:EE:FF               rapport de diagnostic
  Compile d'abord le plugin (plugin\build.ps1).
#>
$ErrorActionPreference = "Stop"
$here   = $PSScriptRoot
$out    = Join-Path $here "bin"
$simhub = if ($env:SIMHUB_INSTALL_PATH) { $env:SIMHUB_INSTALL_PATH } else { "F:\Program Files (x86)\SimHub\" }
$fw     = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$wm     = "C:\Windows\System32\WinMetadata"
$dll    = Join-Path $here "..\..\plugin\bin\Release\User.DafitWatch.dll"

New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item $dll $out -Force
foreach ($f in "SimHub.Logging.dll", "log4net.dll", "SimHub.Plugins.dll", "GameReaderCommon.dll", "Newtonsoft.Json.dll") {
    Copy-Item (Join-Path $simhub $f) $out -Force
}

& "$fw\csc.exe" /nologo /codepage:65001 /out:"$out\WatchClientTest.exe" `
    /r:"$out\User.DafitWatch.dll" /r:"$out\log4net.dll" `
    /r:"$wm\Windows.Devices.winmd" /r:"$wm\Windows.Foundation.winmd" `
    /r:"$fw\System.Runtime.dll" /r:"$fw\System.Threading.Tasks.dll" "$here\Program.cs"
if ($LASTEXITCODE -ne 0) { exit 1 }

[Console]::OutputEncoding = [Text.Encoding]::UTF8
& "$out\WatchClientTest.exe" @args
