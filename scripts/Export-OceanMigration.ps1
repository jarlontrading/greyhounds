[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidateSet('racing','greyhounds')][string]$Trader,
 [string]$SettingsFile,
 [Parameter(Mandatory)][string]$OutputDirectory,
 [switch]$IncludeData,
 [string]$MariaDbBin='C:\Program Files\MariaDB 12.3\bin'
)
$ErrorActionPreference='Stop'
if(!$SettingsFile){$SettingsFile=if($Trader -eq 'racing'){'D:\Dropbox\SportunoPF\projectvariables\SPRacing.env'}else{'D:\Dropbox\SportunoPF\projectvariables\GreyhoundTrader.env'}}
$settings=@{}
foreach($line in Get-Content -LiteralPath $SettingsFile){
 if($line.Trim() -and !$line.TrimStart().StartsWith('#') -and $line.Contains('=')){
  $parts=$line.Split('=',2);$settings[$parts[0].Trim()]=$parts[1].Trim().Trim('"')
 }
}
$prefix=if($Trader -eq 'racing'){'SPRACING'}else{'GREYHOUNDTRADER'}
$dbKey=if($Trader -eq 'racing'){'SPRACING_RACING_DATABASE'}else{'GREYHOUNDTRADER_DATABASE'}
$db=$settings[$dbKey];if(!$db){$db=if($Trader -eq 'racing'){'randg'}else{'greyhoundtrader'}}
if($db -notmatch '^[a-zA-Z0-9_]+$'){throw 'Unexpected database name'}
foreach($key in @('MYSQL_SERVER','MYSQL_USER','MYSQL_PASSWORD')){if(!$settings["${prefix}_$key"]){throw "Missing setting ${prefix}_$key"}}
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $out){throw 'Output directory must be new to prevent overwriting a backup'}
# Keep data exports outside any Git working tree.
$ancestor=[IO.DirectoryInfo]$out
while($ancestor){if(Test-Path -LiteralPath (Join-Path $ancestor.FullName '.git')){throw 'Exports must not be inside a Git working tree'};$ancestor=$ancestor.Parent}
New-Item -ItemType Directory -Path $out|Out-Null
function Native([string]$Exe,[string[]]$Arguments){
 $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $MariaDbBin $Exe))
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $start.Environment['MYSQL_PWD']=$settings["${prefix}_MYSQL_PASSWORD"]
 foreach($arg in $Arguments){$start.ArgumentList.Add($arg)}
 $process=[Diagnostics.Process]::Start($start)
 $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
 $process.WaitForExit()
 if($process.ExitCode -ne 0){throw "$Exe failed ($($process.ExitCode)): $($stderr.GetAwaiter().GetResult())"}
 return $stdout.GetAwaiter().GetResult()
}
$common=@('--connect-timeout=8',"--host=$($settings["${prefix}_MYSQL_SERVER"])","--user=$($settings["${prefix}_MYSQL_USER"])")
$query="SET SESSION max_statement_time=20; SELECT DATABASE() database_name, VERSION() server_version; SELECT table_name,table_type,engine,table_rows,data_length,index_length FROM information_schema.tables WHERE table_schema=DATABASE() ORDER BY table_name; SELECT trigger_name,event_object_table FROM information_schema.triggers WHERE trigger_schema=DATABASE(); SELECT routine_name,routine_type FROM information_schema.routines WHERE routine_schema=DATABASE();"
$inventory=Native 'mariadb.exe' ($common+@('--batch','--raw',"--database=$db","--execute=$query"))
$inventory|Set-Content (Join-Path $out 'inventory.tsv') -Encoding utf8
if($IncludeData -and $inventory -match '\t(?:MyISAM|Aria|MEMORY)\t[1-9][0-9]*\t'){throw 'Populated nontransactional tables need a separate export or coordinated write freeze; no data dump was made.'}
$dump=Join-Path $out 'database.sql'
$argsDump=@($common|Where-Object {$_ -notlike '--connect-timeout=*'})+@('--single-transaction','--skip-lock-tables','--hex-blob',"--result-file=$dump")
if(!$IncludeData){$argsDump+='--no-data'}
Native 'mariadb-dump.exe' ($argsDump+@($db))|Out-Null
@{trader=$Trader;database=$db;schemaOnly=(!$IncludeData);createdUtc=[DateTime]::UtcNow.ToString('O');files=@(Get-ChildItem $out -File|ForEach-Object {@{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $out 'manifest.json') -Encoding utf8
Write-Output "Export complete: $Trader, schemaOnly=$(!$IncludeData), $out. No database configuration changed."


