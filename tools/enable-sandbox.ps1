[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent()
)
$isAdministrator = $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)

if (-not $isAdministrator) {
    $arguments = @(
        '-NoProfile'
        '-ExecutionPolicy'
        'Bypass'
        '-File'
        "`"$PSCommandPath`""
    )
    Start-Process `
        -FilePath 'powershell.exe' `
        -Verb RunAs `
        -ArgumentList $arguments
    exit 0
}

$feature = Get-WindowsOptionalFeature `
    -Online `
    -FeatureName 'Containers-DisposableClientVM'
if ($feature.State -eq 'Enabled') {
    Write-Host 'Windows Sandbox is already enabled.'
    exit 0
}

$result = Enable-WindowsOptionalFeature `
    -Online `
    -FeatureName 'Containers-DisposableClientVM' `
    -All `
    -NoRestart

Write-Host 'Windows Sandbox has been enabled.'
if ($result.RestartNeeded) {
    Write-Host 'Restart Windows before running "Start Rokid Control Test.cmd".'
}
