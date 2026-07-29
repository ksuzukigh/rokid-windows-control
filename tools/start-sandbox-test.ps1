[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishPath = Join-Path $repoRoot 'artifacts\publish\win-x64'
$appPath = Join-Path $publishPath 'Rokid Control.exe'
$adbPath = Join-Path $publishPath 'vendor\scrcpy\adb.exe'
$bootstrapPath = Join-Path $publishPath 'SandboxBootstrap.cmd'
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

$wifiAddress = $null
if (Test-Path -LiteralPath $adbPath) {
    $devicesOutput = & $adbPath devices 2>$null
    $usbSerial = $devicesOutput |
        Select-String -Pattern '^([^\s:]+)\s+device$' |
        ForEach-Object { $_.Matches[0].Groups[1].Value } |
        Select-Object -First 1
    if ($null -ne $usbSerial) {
        $wifiOutput = & $adbPath -s $usbSerial shell ip -f inet addr show wlan0 2>$null
        $wifiMatch = [regex]::Match(
            ($wifiOutput -join [Environment]::NewLine),
            '\binet\s+((?:\d{1,3}\.){3}\d{1,3})/'
        )
        if ($wifiMatch.Success) {
            $wifiAddress = "$($wifiMatch.Groups[1].Value):5555"
        }
    }

    if ($null -eq $wifiAddress) {
        $mdnsOutput = & $adbPath mdns services 2>$null
        $mdnsMatch = [regex]::Match(
            ($mdnsOutput -join [Environment]::NewLine),
            '(?m)\b(?:\d{1,3}\.){3}\d{1,3}:5555\b'
        )
        if ($mdnsMatch.Success) {
            $wifiAddress = $mdnsMatch.Value
        }
    }
}

$bootstrapLines = @(
    '@echo off'
    'set "DATA=%LOCALAPPDATA%\Rokid Control"'
    'if not exist "%DATA%" mkdir "%DATA%"'
)
if ($null -ne $wifiAddress) {
    $bootstrapLines += "> `"%DATA%\wifi-address.txt`" echo $wifiAddress"
}
$bootstrapLines += 'start "" "C:\RokidControl\Rokid Control.exe"'
Set-Content `
    -LiteralPath $bootstrapPath `
    -Value $bootstrapLines `
    -Encoding ascii

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
    <Command>cmd.exe /c C:\RokidControl\SandboxBootstrap.cmd</Command>
  </LogonCommand>
</Configuration>
"@
Set-Content -LiteralPath $sandboxConfigPath -Value $configuration -Encoding utf8

Start-Process -FilePath $sandboxConfigPath
Write-Host 'Rokid Control is starting in Windows Sandbox.'
