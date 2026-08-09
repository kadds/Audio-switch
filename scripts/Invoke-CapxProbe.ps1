param(
    [string]$EndpointFile = (Join-Path (Split-Path -Parent $PSScriptRoot) 'work\endpoint-realtek.txt'),
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) ('work\capx-probe-{0}.json' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [switch]$Build,
    [switch]$SkipBuild
)

$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'src\CapxProbe.cs'
$exe = Join-Path $projectRoot 'tools\bin\CapxProbe.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $EndpointFile)) {
    throw "Endpoint file not found: $EndpointFile"
}

if (-not $SkipBuild -and ($Build -or -not (Test-Path -LiteralPath $exe) -or (Get-Item -LiteralPath $source).LastWriteTime -gt (Get-Item -LiteralPath $exe).LastWriteTime)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $exe) | Out-Null
    & $csc /nologo /optimize+ /target:exe /platform:x64 /out:$exe $source
    if ($LASTEXITCODE -ne 0) { throw "C# compile failed: $LASTEXITCODE" }
}

$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$argumentString = "`"@$EndpointFile`" `"$OutputPath`""

Invoke-CommandInDesktopPackage `
    -PackageFamilyName 'DolbyLaboratories.DolbyAccess_rz1tebttyb220' `
    -AppId 'App' `
    -Command $exe `
    -Args $argumentString | Out-Null

for ($i = 0; $i -lt 40; $i++) {
    if (Test-Path -LiteralPath $OutputPath) { break }
    Start-Sleep -Milliseconds 250
}

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "Probe did not produce output within 10 seconds: $OutputPath"
}

Get-Content -Raw -LiteralPath $OutputPath
