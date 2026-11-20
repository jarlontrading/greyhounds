$ErrorActionPreference = 'SilentlyContinue'
$baseUrl = 'http://127.0.0.1:5287/api/status'
$starter = Join-Path $PSScriptRoot 'Start-GreyhoundTrader-Web.ps1'
$log = 'D:\Temp\greyhoundtrader-watchdog.log'

$healthy = $false
try {
    $response = Invoke-WebRequest -UseBasicParsing -Uri $baseUrl -TimeoutSec 8
    $healthy = $response.StatusCode -eq 200
}
catch { }

if ($healthy) { exit 0 }

Add-Content -LiteralPath $log -Value "$(Get-Date -Format o) Web/API unavailable; starting GreyhoundTrader."
if (Test-Path -LiteralPath $starter) {
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$starter`"" `
        -WindowStyle Hidden
}
