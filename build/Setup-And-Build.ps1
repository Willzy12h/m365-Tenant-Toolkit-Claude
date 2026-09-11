#Requires -Version 5.1
<#
.SYNOPSIS
  One-click first build for a machine with no .NET SDK. Needs no administrator rights.
.DESCRIPTION
  1. Uses an existing .NET 8 SDK if one is on PATH; otherwise downloads Microsoft's dotnet-install.ps1 and installs
     the .NET 8 SDK into <repo>\.dotnet (user-local, nothing written outside the folder, no PATH change).
  2. Runs build\Build-Portable.ps1 (restore, build, test, manifest, publish, package).
  3. Reports where the portable folder and Start.cmd are.
  Internet access to dot.net / builds.dotnet.microsoft.com and nuget.org is required for the first run only.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
# Everything shown on screen is also written to build\last-build.log so the output can be read back without copying it.
$logFile = Join-Path $PSScriptRoot 'last-build.log'
try { Start-Transcript -Path $logFile -Force | Out-Null } catch { }
Write-Host "BDIT Tenant Toolkit - first build in $root" -ForegroundColor Cyan
Write-Host "Full output is being written to $logFile"

function Test-Sdk8([string]$exe) {
    if (-not (Test-Path -LiteralPath $exe)) { return $false }
    try { $sdks = & $exe --list-sdks 2>$null; return [bool]($sdks | Where-Object { $_ -match '^8\.' }) } catch { return $false }
}

$localSdk = Join-Path $root '.dotnet'
$localExe = Join-Path $localSdk 'dotnet.exe'
$useLocal = $false

$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath -and (Test-Sdk8 $onPath.Source)) {
    Write-Host "Using .NET 8 SDK already installed at $($onPath.Source)"
} elseif (Test-Sdk8 $localExe) {
    Write-Host "Using user-local .NET 8 SDK at $localExe"
    $useLocal = $true
} else {
    Write-Host "No .NET 8 SDK found. Installing a user-local copy into $localSdk (no administrator rights, nothing outside this folder)..." -ForegroundColor Yellow
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -Channel 8.0 -InstallDir $localSdk -NoPath
    if (-not (Test-Sdk8 $localExe)) { throw "The .NET 8 SDK install did not complete. Check internet access and re-run, or install the SDK manually from https://dotnet.microsoft.com/download/dotnet/8.0" }
    $useLocal = $true
}

if ($useLocal) {
    $env:PATH = "$localSdk;$env:PATH"
    $env:DOTNET_ROOT = $localSdk
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$buildArgs = @()
if ($SkipTests) { $buildArgs += '-SkipTests' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-Portable.ps1') @buildArgs 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) {
    try { Stop-Transcript | Out-Null } catch { }
    throw "Build-Portable.ps1 failed with exit code $LASTEXITCODE. Scroll up for the first error; compile errors are listed as 'error CS....'. The full log is in $logFile."
}

[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
$stage = Join-Path $root "dist\BDIT-Tenant-Toolkit-$version-win-x64"
Write-Host ""
Write-Host "Ready. Portable folder: $stage" -ForegroundColor Green
Write-Host "Run:   $stage\Start.cmd"
Write-Host "ZIP:   $stage.zip  (copy this to other engineers)"
try { Stop-Transcript | Out-Null } catch { }
