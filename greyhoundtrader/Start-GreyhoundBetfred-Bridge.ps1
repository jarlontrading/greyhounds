$ErrorActionPreference = 'Stop'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5278'
$env:BETFRED_SPORT_ID = '70'
$env:BETFRED_RACING_SLUG = 'greyhound-racing?tab=today'
$env:BETFRED_BROWSER_PROFILE = 'D:\Temp\greyhoundtrader\betfred-pricing-browser-profile-v2'

$bridgeRoot = 'D:\Temp\greyhound-betfred-bridge-current'
$bridge = Join-Path $bridgeRoot 'GreyhoundTrader.Bet365Capture.dll'
$bridgeLog = "D:\Temp\greyhound-betfred-bridge-$PID.log"

Set-Location -LiteralPath $bridgeRoot
# Never rotate a large Dropbox-synchronised log on the startup path.
# This bridge copy only serves the GreyhoundTrader Betfred endpoints.  Without
# this switch it also opens the shared Bet365 racing profile used by the horse
# service, and Chromium can block while trying to reuse that live profile.
if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains 5278) { exit 0 }
Start-Process -FilePath 'C:\Program Files\dotnet\dotnet.exe' `
    -ArgumentList @("`"$bridge`"", '--CaptureProvider', 'betfred-only') `
    -WorkingDirectory $bridgeRoot -WindowStyle Hidden `
    -RedirectStandardOutput $bridgeLog -RedirectStandardError ($bridgeLog + '.err')
