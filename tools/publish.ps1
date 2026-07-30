[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0-alpha.2'
)

$ErrorActionPreference = 'Stop'

function Assert-UnixLineEndings {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Contains([byte]13)) {
        throw "$Path must use Unix LF line endings."
    }
}

$numericVersionMatch = [regex]::Match($Version, '^(\d+)\.(\d+)\.(\d+)')
$numericVersion = '{0}.{1}.{2}.0' -f `
    $numericVersionMatch.Groups[1].Value, `
    $numericVersionMatch.Groups[2].Value, `
    $numericVersionMatch.Groups[3].Value
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($null -ne $dotnetCommand) {
    $dotnetCommand.Source
}
else {
    Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
}
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw '.NET 10 SDK was not found.'
}

$watchdogSource = Join-Path $repoRoot `
    'src\RokidControl.App\Resources\rokid_windows_wifi_watchdog.sh'
Assert-UnixLineEndings -Path $watchdogSource

$vendor = Join-Path $repoRoot 'vendor\scrcpy\scrcpy.exe'
if (-not (Test-Path -LiteralPath $vendor)) {
    & (Join-Path $PSScriptRoot 'prepare-vendor.ps1')
}

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$output = Join-Path $repoRoot 'artifacts\publish\win-x64'
Push-Location $repoRoot
try {
    & $dotnet publish 'src\RokidControl.App\RokidControl.App.csproj' `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $output `
        --configfile 'NuGet.Config' `
        "-p:Version=$Version" `
        "-p:AssemblyVersion=$numericVersion" `
        "-p:FileVersion=$numericVersion" `
        "-p:InformationalVersion=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$requiredFiles = @(
    (Join-Path $output 'Rokid Control.exe'),
    (Join-Path $output 'vendor\scrcpy\adb.exe'),
    (Join-Path $output 'vendor\scrcpy\scrcpy.exe'),
    (Join-Path $output 'Resources\rokid_windows_wifi_watchdog.sh'),
    (Join-Path $output 'Licenses\Rokid-Control-LICENSE'),
    (Join-Path $output 'Licenses\THIRD-PARTY-NOTICES.md')
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile)) {
        throw "Publish output is missing $requiredFile."
    }
}

$publishedWatchdog = Join-Path $output `
    'Resources\rokid_windows_wifi_watchdog.sh'
Assert-UnixLineEndings -Path $publishedWatchdog

Write-Host "Published self-contained Windows x64 package to $output"
