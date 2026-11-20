$ErrorActionPreference = 'Stop'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:GREYHOUNDTRADER_SETTINGS_FILE = 'D:\Dropbox\SportunoPF\projectvariables\GreyhoundTrader.env'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$webRoot = Join-Path $projectRoot 'GreyhoundTrader.Web'
$web = Join-Path $webRoot 'bin\Debug\net8.0\GreyhoundTrader.Web.dll'
$startupLog = "D:\Temp\greyhoundtrader-startup-$PID.log"

# Keep exactly one launcher (and therefore one web host) alive. Scheduled-logon
# startup and the watchdog can overlap while Windows and the database are still
# warming up after the daily reboot.
$launcherLockPath = 'D:\Temp\greyhoundtrader-web.lock'
try {
    $launcherLock = [IO.File]::Open($launcherLockPath, 'OpenOrCreate', 'ReadWrite', 'None')
}
catch {
    exit 0
}
if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains 5287) {
    $launcherLock.Dispose()
    exit 0
}

try {
    Start-Process -FilePath 'C:\Program Files\dotnet\dotnet.exe' `
        -ArgumentList @("`"$web`"", '--RacingTrader:Url', 'http://127.0.0.1:5287') -WorkingDirectory $webRoot `
        -WindowStyle Hidden -RedirectStandardOutput $startupLog `
        -RedirectStandardError ($startupLog + '.err')
}
finally {
    $launcherLock.Dispose()
}
