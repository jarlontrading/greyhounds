[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('racing','greyhounds')][string]$Trader,[switch]$TestWrite)
$ErrorActionPreference='Stop'
$folder=if($Trader -eq 'racing'){'RacingTrader'}else{'GreyhoundTrader'}
$path=Join-Path $env:LOCALAPPDATA "Sportuno\$folder\ocean-connection.json"
$config=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
$base=[uri]$config.url
if($base.Scheme -ne 'https' -or $base.Host -ne 'cricket-single-event-test-c5b6o.ondigitalocean.app'){throw 'Unexpected Ocean endpoint; refusing to send credentials'}
Add-Type -AssemblyName System.Security
$key=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($config.keyDpapi),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
$headers=@{'X-Sportuno-Admin-Key'=$key}
$health=Invoke-RestMethod "$($config.url)/health" -Headers $headers -TimeoutSec 15 -MaximumRedirection 0
$roundTrip=$false;$conflictRejected=$false
if($TestWrite){
 if($config.namespace -notmatch '^(racing|greyhounds)-(prod|uat)$'){throw 'Unexpected storage namespace'}
 $url="$($config.url)/shared-state/$($config.namespace)-migration-probe-v1"
 $previous=Invoke-RestMethod $url -Headers $headers -TimeoutSec 15 -MaximumRedirection 0
 $doc=@{schemaVersion=1;purpose='migration-storage-probe';operationId=[guid]::NewGuid().ToString();utc=[DateTime]::UtcNow.ToString('O');publishesTradingMessages=$false}|ConvertTo-Json -Compress
 $body=@{json=$doc;revision=$previous.revision}|ConvertTo-Json -Compress
 $saved=Invoke-RestMethod $url -Method Put -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 15 -MaximumRedirection 0
 $read=Invoke-RestMethod $url -Headers $headers -TimeoutSec 15 -MaximumRedirection 0
 $expected=$doc|ConvertFrom-Json;$actual=$read.json|ConvertFrom-Json
 if($read.revision -ne $saved.revision){throw 'Ocean revision verification failed'}
 foreach($property in $expected.PSObject.Properties){if($actual.($property.Name) -cne $property.Value){throw 'Ocean document verification failed'}}
 $roundTrip=$true
 try{Invoke-RestMethod $url -Method Put -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 15 -MaximumRedirection 0|Out-Null}
 catch{if([int]$_.Exception.Response.StatusCode -eq 409){$conflictRejected=$true}else{throw}}
 if(!$conflictRejected){throw 'Ocean accepted a stale document revision'}
}
[pscustomobject]@{trader=$Trader;database=$health.database;apiTarget=$health.target;authenticated=$true;roundTrip=$roundTrip;staleRevisionRejected=$conflictRejected;liveStorageChanged=$false}|ConvertTo-Json -Compress

