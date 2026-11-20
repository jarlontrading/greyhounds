[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$Sha256,
    [string]$ReleaseRoot='D:\TraderReleases'
)
$ErrorActionPreference='Stop'
$packagePath=(Resolve-Path -LiteralPath $Package).Path
if((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $Sha256){throw 'Package checksum mismatch'}
$staging=Join-Path ([IO.Path]::GetTempPath()) ('trader-release-'+[guid]::NewGuid())
Expand-Archive -LiteralPath $packagePath -DestinationPath $staging
$manifest=Get-Content (Join-Path $staging 'manifest.json') -Raw|ConvertFrom-Json
if($manifest.dirty){throw 'Refusing a release built from an uncommitted working tree'}
if($manifest.version -notmatch '^v\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$'){throw 'Invalid manifest version'}
$prefix=[IO.Path]::GetFullPath($staging)+[IO.Path]::DirectorySeparatorChar
foreach($file in $manifest.files){
    $path=[IO.Path]::GetFullPath((Join-Path $staging $file.path))
    if(!$path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest path escapes release'}
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256){throw "File checksum mismatch: $($file.path)"}
}
$target=Join-Path $ReleaseRoot $manifest.version
if(Test-Path -LiteralPath $target){throw "Release already exists: $target"}
New-Item -ItemType Directory -Force -Path $ReleaseRoot|Out-Null
Move-Item -LiteralPath $staging -Destination $target
Write-Output "Verified and staged $($manifest.version) ($($manifest.commit)) at $target. No running services changed."
