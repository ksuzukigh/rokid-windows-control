[CmdletBinding()]
param(
    [switch]$SkipRestore
)

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

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location $repoRoot
try {
    if (-not $SkipRestore) {
        & $dotnet restore 'RokidControl.Windows.sln' --configfile 'NuGet.Config'
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed with exit code $LASTEXITCODE."
        }
    }

    & $dotnet build 'RokidControl.Windows.sln' --configuration Release --no-restore -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    & $dotnet run --project 'tests\RokidControl.SelfTest\RokidControl.SelfTest.csproj' `
        --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Self-tests failed with exit code $LASTEXITCODE."
    }

    & $dotnet run --project 'tools\RokidControl.CaptureProbe\RokidControl.CaptureProbe.csproj' `
        --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Windows Graphics Capture probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
