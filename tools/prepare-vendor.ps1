[CmdletBinding()]
param(
    [string]$Version = '4.1',
    [string]$ExpectedSha256 = '5b12172b3264b2889f4583ee64752ce832e29bc8b1089dca81093459697165db'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$archiveName = "scrcpy-win64-v$Version.zip"
$downloadDirectory = Join-Path $repoRoot 'artifacts\downloads'
$archivePath = Join-Path $downloadDirectory $archiveName
$vendorDirectory = Join-Path $repoRoot 'vendor'
$expandedDirectory = Join-Path $vendorDirectory "scrcpy-win64-v$Version"
$targetDirectory = Join-Path $vendorDirectory 'scrcpy'
$downloadUri = "https://github.com/Genymobile/scrcpy/releases/download/v$Version/$archiveName"

New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $vendorDirectory | Out-Null

if (-not (Test-Path -LiteralPath $archivePath)) {
    Write-Host "Downloading official scrcpy $Version for Windows x64..."
    Invoke-WebRequest -Uri $downloadUri -OutFile $archivePath
}

$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $ExpectedSha256.ToLowerInvariant()) {
    throw "SHA-256 mismatch for $archiveName. Expected $ExpectedSha256, actual $actualHash."
}

if (Test-Path -LiteralPath $expandedDirectory) {
    Remove-Item -LiteralPath $expandedDirectory -Recurse -Force
}

Expand-Archive -LiteralPath $archivePath -DestinationPath $vendorDirectory -Force

if (Test-Path -LiteralPath $targetDirectory) {
    Remove-Item -LiteralPath $targetDirectory -Recurse -Force
}

Move-Item -LiteralPath $expandedDirectory -Destination $targetDirectory

$requiredFiles = @('adb.exe', 'scrcpy.exe', 'scrcpy-server')
foreach ($requiredFile in $requiredFiles) {
    $requiredPath = Join-Path $targetDirectory $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "The official archive did not contain $requiredFile."
    }
}

Write-Host "Verified and prepared scrcpy $Version at $targetDirectory"
