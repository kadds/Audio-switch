[CmdletBinding()]
param(
    [string]$EndpointFile = (Join-Path (Split-Path -Parent $PSScriptRoot) 'work\endpoint-realtek.txt'),
    [ValidateSet('Dynamic', 'Game', 'Movie', 'Music', 'Voice', 'Custom1', 'Custom2', 'Custom3')]
    [string]$Profile = 'Game',
    [string]$OutputPath = '',
    [switch]$Apply,
    [switch]$Build,
    [switch]$SkipBuild
)

$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'src\CapxSetProfile.cs'
$exe = Join-Path $projectRoot 'tools\bin\CapxSetProfile.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$profileHex = @{
    Dynamic = '0100000000'
    Game    = '0101000000'
    Movie   = '0102000000'
    Music   = '0103000000'
    Voice   = '0104000000'
    Custom1 = '0105000000'
    Custom2 = '0106000000'
    Custom3 = '0107000000'
}[$Profile]

if (-not (Test-Path -LiteralPath $EndpointFile)) { throw "Endpoint file not found: $EndpointFile" }
if (-not $SkipBuild -and ($Build -or -not (Test-Path -LiteralPath $exe) -or (Get-Item $source).LastWriteTime -gt (Get-Item $exe).LastWriteTime)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $exe) | Out-Null
    & $csc /nologo /optimize+ /target:exe /platform:x64 /out:$exe $source
    if ($LASTEXITCODE -ne 0) { throw "C# compile failed: $LASTEXITCODE" }
}

if ([String]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $projectRoot ('work\capx-set-{0}-{1}.json' -f $Profile.ToLowerInvariant(), (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$quote = [char]34
$applyArgument = if ($Apply) { ' --apply' } else { '' }
$argumentString = $quote + '@' + $EndpointFile + $quote + ' ' + $quote + $profileHex + $quote + ' ' + $quote + $OutputPath + $quote + $applyArgument

Invoke-CommandInDesktopPackage -PackageFamilyName 'DolbyLaboratories.DolbyAccess_rz1tebttyb220' -AppId 'App' -Command $exe -Args $argumentString | Out-Null
for ($i = 0; $i -lt 40; $i++) {
    if (Test-Path -LiteralPath $OutputPath) { break }
    Start-Sleep -Milliseconds 250
}
if (-not (Test-Path -LiteralPath $OutputPath)) { throw "CAPX setter did not produce output within 10 seconds: $OutputPath" }
Get-Content -Raw -LiteralPath $OutputPath
