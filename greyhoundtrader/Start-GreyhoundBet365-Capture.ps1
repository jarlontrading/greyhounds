$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$captureRoot = Join-Path $projectRoot 'GreyhoundTrader.Bet365Capture'
$captureDll = Join-Path $captureRoot 'bin\Debug\net8.0\GreyhoundTrader.Bet365Capture.dll'
$captureLog = "D:\Temp\greyhoundtrader\bet365-greyhound-service-$PID.log"
$launcherLockPath = 'D:\Temp\greyhoundtrader\bet365-greyhound-service.lock'

try {
    $launcherLock = [IO.File]::Open($launcherLockPath, 'OpenOrCreate', 'ReadWrite', 'None')
}
catch {
    exit 0
}
if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains 5378) {
    $launcherLock.Dispose()
    exit 0
}

if (-not (Test-Path -LiteralPath $captureDll)) {
    & 'C:\Program Files\dotnet\dotnet.exe' build (Join-Path $captureRoot 'GreyhoundTrader.Bet365Capture.vbproj') -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'Greyhound Bet365 capture build failed.' }
}

$logFolder = Split-Path -Parent $captureLog
New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
# Never move or delete logs before binding the capture port.

Set-Location -LiteralPath $captureRoot
try {
    & 'C:\Program Files\dotnet\dotnet.exe' $captureDll *>> $captureLog
}
finally {
    $launcherLock.Dispose()
}
