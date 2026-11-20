$ErrorActionPreference = 'Stop'
$port = 5378
$listener = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue |
    Select-Object -First 1

if ($listener) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
    if ($process.CommandLine -like '*GreyhoundTrader.Bet365Capture*') {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

Start-Process -FilePath 'powershell.exe' `
    -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',
        'D:\Dropbox\BetfairBetdaq\SportunoPF\greyhoundtrader\Start-GreyhoundBet365-Capture.ps1' `
    -WindowStyle Hidden
