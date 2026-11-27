param([string]$Runtime = 'D:\Sportuno\CancellationAlerts',
      [string]$StateDirectory = 'D:\CodexData\cancellation-alerts',
      [string]$Python = '')
$ErrorActionPreference='Stop'
if(!$Python){$Python=(& python -c 'import sys; print(sys.executable)').Trim()}
$pythonw=Join-Path (Split-Path $Python) 'pythonw.exe'
if(!(Test-Path -LiteralPath $pythonw)){throw 'pythonw.exe is required for hidden startup'}
New-Item -ItemType Directory -Path $Runtime -Force | Out-Null
$configPath=Join-Path $Runtime 'config.json'
if(!(Test-Path -LiteralPath $configPath)){
    @{
        stateDirectory=$StateDirectory
        telegramFile='D:\Dropbox\SportunoPF\projectvariables\SPRacing.Telegram.env'
        mariaClient='C:\Program Files\MariaDB 12.3\bin\mariadb.exe'
        startUtc=[datetime]::UtcNow.ToString('o')
        apps=@(
            @{name='Racing';settingsFile='D:\Dropbox\SportunoPF\projectvariables\SPRacing.env';prefix='SPRACING';databaseKey='SPRACING_RACING_DATABASE';defaultDatabase='randg'},
            @{name='Greyhounds';settingsFile='D:\Dropbox\SportunoPF\projectvariables\GreyhoundTrader.env';prefix='GREYHOUNDTRADER';databaseKey='GREYHOUNDTRADER_DATABASE';defaultDatabase='greyhoundtrader'}
        )
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding utf8
}
# Verify read access before replacing a running observer. No Telegram sends here.
& $Python "$PSScriptRoot\cancellation_alerts.py" --config $configPath --read-only
if($LASTEXITCODE -ne 0){throw 'Read-only database preflight failed'}
$taskName='Sportuno - Cancellation Telegram Alerts'
if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){Stop-ScheduledTask -TaskName $taskName}
Copy-Item -LiteralPath "$PSScriptRoot\cancellation_alerts.py" -Destination (Join-Path $Runtime 'cancellation_alerts.py') -Force
$action=New-ScheduledTaskAction -Execute $pythonw -Argument ('"'+(Join-Path $Runtime 'cancellation_alerts.py')+'" --config "'+$configPath+'"') -WorkingDirectory $Runtime
$user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
$principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([timespan]::Zero) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Read-only observer of committed racing/greyhound cancellations; durable Telegram notices to SPORTUNO ALERT.' -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Output "Started $taskName; status: $StateDirectory\status.json"
