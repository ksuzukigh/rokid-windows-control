[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishScript = Join-Path $PSScriptRoot 'publish.ps1'
$publishDirectory = Join-Path $repoRoot 'artifacts\publish\win-x64'
$packageDirectory = Join-Path $repoRoot 'artifacts\package'

$repoFullPath = [IO.Path]::GetFullPath($repoRoot)
$packageFullPath = [IO.Path]::GetFullPath($packageDirectory)
$expectedPrefix = $repoFullPath.TrimEnd(
    [IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $packageFullPath.StartsWith(
        $expectedPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output resolved outside the repository.'
}

& $publishScript

if (Test-Path -LiteralPath $packageFullPath) {
    Remove-Item -LiteralPath $packageFullPath -Recurse -Force
}
New-Item -ItemType Directory -Path $packageFullPath | Out-Null

$archiveName = "Rokid-Control-Windows-x64-$Version.zip"
$archivePath = Join-Path $packageFullPath $archiveName
Compress-Archive `
    -Path (Join-Path $publishDirectory '*') `
    -DestinationPath $archivePath `
    -CompressionLevel Optimal

$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
$checksumPath = "$archivePath.sha256"
"$($hash.Hash.ToLowerInvariant())  $archiveName" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "Created $archivePath"
Write-Host "Created $checksumPath"
