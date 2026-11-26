[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$')][string]$Version)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    function Run([string[]]$Arguments) { & dotnet @Arguments; if($LASTEXITCODE -ne 0){throw "dotnet $Arguments failed ($LASTEXITCODE)"} }
    $out=Join-Path $root "artifacts/$Version"
    if(Test-Path $out){throw "Release directory already exists: $out. Use a new version."}
    Run @('run','--project','greyhoundtrader/GreyhoundTrader.DistanceTests/GreyhoundTrader.DistanceTests.csproj','-c','Release')
    Run @('run','--project','PlacePricingTests/PlacePricingTests.csproj','-c','Release')
    $projects=[ordered]@{
        greyhounds='greyhoundtrader/GreyhoundTrader.Web/GreyhoundTrader.Web.csproj'
    }
    $sha=(& git rev-parse HEAD); if($LASTEXITCODE -ne 0){throw 'Cannot identify source commit'}
    $dirty= [bool](& git status --porcelain)
    foreach($name in $projects.Keys){
        $target=Join-Path $out $name
        Run @('publish',$projects[$name],'-c','Release','--self-contained','false','-o',$target,"-p:Version=$($Version.TrimStart('v'))","-p:SourceRevisionId=$sha")
    }
    $files=@(Get-ChildItem $out -File -Recurse|ForEach-Object {
        @{path=[IO.Path]::GetRelativePath($out,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    @{version=$Version;commit=$sha;dirty=$dirty;createdUtc=[DateTime]::UtcNow.ToString('O');files=$files}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $out 'manifest.json') -Encoding utf8
    Compress-Archive -Path "$out/*" -DestinationPath "$out.zip"
    Get-FileHash "$out.zip" -Algorithm SHA256|Format-List
} finally {Pop-Location}
