[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
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
        --configfile 'NuGet.Config'
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

Write-Host "Published self-contained Windows x64 package to $output"
