[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AndroidJar,
    [Parameter(Mandatory)][string]$D8Jar
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $repoRoot ('build\device-helper\' + [guid]::NewGuid().ToString('N'))
$classes = Join-Path $buildDirectory 'classes'
$dex = Join-Path $buildDirectory 'dex'
New-Item -ItemType Directory -Path $classes,$dex -Force | Out-Null
$source = Join-Path $repoRoot 'DeviceHelper\RokidUiReader.java'
$classesJar = Join-Path $buildDirectory 'classes.jar'
$artifact = Join-Path $repoRoot 'src\RokidControl.App\Resources\rokid_ui_reader.jar'
& javac --release 8 -encoding UTF-8 -cp $AndroidJar -d $classes $source
if ($LASTEXITCODE -ne 0) { throw 'Java compilation failed.' }
& jar cf $classesJar -C $classes .
if ($LASTEXITCODE -ne 0) { throw 'Class packaging failed.' }
& java -cp $D8Jar com.android.tools.r8.D8 --min-api 24 --lib $AndroidJar --output $dex $classesJar
if ($LASTEXITCODE -ne 0) { throw 'DEX compilation failed.' }
& jar cf $artifact -C $dex classes.dex
if ($LASTEXITCODE -ne 0) { throw 'DEX packaging failed.' }
[ordered]@{
    source_sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    jar_sha256 = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repoRoot 'DeviceHelper\checksums.json') -Encoding utf8
Write-Output $artifact
