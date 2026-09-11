#Requires -Version 5.1
<#
.SYNOPSIS
  Regenerates standards/manifest.json with SHA-256 digests of every Build Standard release file.
.DESCRIPTION
  The toolkit refuses to load a standard whose digest is missing or does not match the manifest.
  Run this after editing or adding a standard, then review the diff before committing.
  The manifest is an integrity digest, not a cryptographic signature; the release ZIP checksum
  (SHA256SUMS.txt and the .sha256 file) protects it in transit.
#>
[CmdletBinding()]
param(
    [string]$StandardsDirectory,
    [string]$GeneratedBy = "$env:USERNAME@$env:COMPUTERNAME"
)
$ErrorActionPreference = 'Stop'
if (-not $StandardsDirectory) { $StandardsDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'standards' }
if (-not (Test-Path -LiteralPath $StandardsDirectory -PathType Container)) { throw "Standards directory not found: $StandardsDirectory" }

$files = [ordered]@{}
Get-ChildItem -LiteralPath $StandardsDirectory -Filter '*.json' | Where-Object { $_.Name -ne 'manifest.json' } | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $files[$_.Name] = $hash
    Write-Host ("  {0}  {1}" -f $hash, $_.Name)
}
if ($files.Count -eq 0) { throw 'No standard release files found.' }

$manifest = [ordered]@{
    algorithm   = 'SHA-256'
    generatedAt = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
    generatedBy = $GeneratedBy
    noteText    = 'SHA-256 integrity digests of the BDIT Build Standard release files. Detects modification after release; this is not a cryptographic signature.'
    files       = $files
}
$path = Join-Path $StandardsDirectory 'manifest.json'
$json = $manifest | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($path, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Manifest written: $path"
