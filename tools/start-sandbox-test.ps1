[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishPath = Join-Path $repoRoot 'artifacts\publish\win-x64'
$appPath = Join-Path $publishPath 'Rokid Control.exe'
$sandboxDirectory = Join-Path $repoRoot 'artifacts\sandbox'
$sandboxConfigPath = Join-Path $sandboxDirectory 'RokidControl.wsb'

$sandboxExecutable = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
if (-not (Test-Path -LiteralPath $sandboxExecutable)) {
    throw 'Windows Sandbox is not enabled. Run "Enable Windows Sandbox.cmd" first, then restart Windows.'
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'publish.ps1')
}
if (-not (Test-Path -LiteralPath $appPath)) {
    throw "The test build was not found at $appPath."
}

New-Item -ItemType Directory -Path $sandboxDirectory -Force | Out-Null
$escapedPublishPath = [Security.SecurityElement]::Escape($publishPath)
$configuration = @"
<Configuration>
  <VGpu>Enable</VGpu>
  <Networking>Enable</Networking>
  <ClipboardRedirection>Enable</ClipboardRedirection>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$escapedPublishPath</HostFolder>
      <SandboxFolder>C:\RokidControl</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>cmd.exe /c start "" "C:\RokidControl\Rokid Control.exe"</Command>
  </LogonCommand>
</Configuration>
"@
Set-Content -LiteralPath $sandboxConfigPath -Value $configuration -Encoding utf8

Start-Process -FilePath $sandboxConfigPath
Write-Host 'Rokid Control is starting in Windows Sandbox.'
