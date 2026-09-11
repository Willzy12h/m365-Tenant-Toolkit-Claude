#Requires -Version 5.1
<#
.SYNOPSIS
  Restores, builds, tests, publishes and packages the BDIT Tenant Toolkit as a portable ZIP.
.DESCRIPTION
  Steps: dotnet restore -> dotnet build -c Release -> dotnet test -> regenerate standards manifest ->
  dotnet publish (self-contained win-x64, framework-dependent runtime NOT required on the engineer's PC) ->
  stage standards/config/docs/launchers -> write VERSION.json and SHA256SUMS.txt -> zip.
  Requires the .NET 8 SDK on the build machine only. Engineers never need the SDK.
.PARAMETER SkipTests
  Skip the test step (not recommended for a release).
.PARAMETER Runtime
  Runtime identifier for the publish step. Default win-x64.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw 'The .NET SDK (dotnet) is not on PATH. Install the .NET 8 SDK on the build machine: https://dotnet.microsoft.com/download/dotnet/8.0' }
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^8\.' })) { throw "No .NET 8 SDK found. Installed SDKs:`n$($sdks -join "`n")" }

[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw 'Version not found in Directory.Build.props' }

$dist = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $root 'dist' }
$stageName = "BDIT-Tenant-Toolkit-$version-$Runtime"
$stage = Join-Path $dist $stageName
$zip = Join-Path $dist "$stageName.zip"
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Invoke-Step 'Restore' { & dotnet restore BDIT.TenantToolkit.sln }
Invoke-Step 'Build' { & dotnet build BDIT.TenantToolkit.sln -c $Configuration --no-restore }
if (-not $SkipTests) {
    Invoke-Step 'Test' { & dotnet test tests\BDIT.TenantToolkit.Tests\BDIT.TenantToolkit.Tests.csproj -c $Configuration --no-build --nologo --logger 'trx;LogFileName=test-results.trx' --results-directory (Join-Path $dist 'test-results') }
}
Invoke-Step 'Standards manifest' { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Update-StandardsManifest.ps1') -GeneratedBy "Build-Portable $version" }
Invoke-Step 'Publish application (self-contained)' {
    & dotnet publish src\BDIT.TenantToolkit.App\BDIT.TenantToolkit.App.csproj -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -o (Join-Path $stage 'app')
}

Invoke-Step 'Stage package contents' {
    foreach ($dir in 'standards', 'config', 'docs') {
        Copy-Item -LiteralPath (Join-Path $root $dir) -Destination (Join-Path $stage $dir) -Recurse -Force
    }
    foreach ($dir in 'data', 'logs', 'reports') { New-Item -ItemType Directory -Force -Path (Join-Path $stage $dir) | Out-Null }
    Get-ChildItem -LiteralPath (Join-Path $root 'packaging') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stage -Force }
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stage -Force
    Copy-Item -LiteralPath (Join-Path $root 'CHANGELOG.md') -Destination $stage -Force

    $runtimeVersion = (Get-ChildItem -LiteralPath (Join-Path $stage 'app') -Filter 'System.Private.CoreLib.dll' -Recurse | Select-Object -First 1).VersionInfo.ProductVersion
    $manifest = [ordered]@{
        product        = 'BDIT Microsoft 365 Tenant Toolkit'
        version        = $version
        runtime        = $Runtime
        selfContained  = $true
        dotnetRuntime  = $runtimeVersion
        dotnetSdk      = (& dotnet --version)
        builtAt        = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        builtOn        = $env:COMPUTERNAME
        standards      = (Get-ChildItem -LiteralPath (Join-Path $stage 'standards') -Filter '*.json' | Where-Object { $_.Name -ne 'manifest.json' } | ForEach-Object { $_.Name })
        nugetPackages  = @(
            @{ name = 'Microsoft.Identity.Client'; version = '4.89.0' },
            @{ name = 'System.Security.Cryptography.ProtectedData'; version = '8.0.0' }
        )
        note           = 'Internal BDIT tool, unsigned. SHA256SUMS.txt and the ZIP .sha256 file detect modification in transit; verify them before first use. Allow app\BDIT.TenantToolkit.App.exe in any application-control policy by path or hash.'
    }
    [IO.File]::WriteAllText((Join-Path $stage 'VERSION.json'), (($manifest | ConvertTo-Json -Depth 5) + "`n"), [Text.UTF8Encoding]::new($false))
}

Invoke-Step 'Checksums' {
    $lines = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
    }
    [IO.File]::WriteAllText((Join-Path $stage 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    Write-Host ("{0} files listed" -f $lines.Count)
}

Invoke-Step 'Zip' {
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    $zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zip.sha256", "$zipHash  $stageName.zip`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Package: $zip"
    Write-Host "SHA-256: $zipHash"
}

Write-Host ""
Write-Host "Portable release ready: $stage" -ForegroundColor Green
