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
    function Invoke-AdbRead {
        param([string[]]$Arguments)

        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & $adbPath @Arguments 2>$null
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    }

    $devicesOutput = Invoke-AdbRead -Arguments @('devices')
    $usbWifiIp = $null
    $usbSerial = $devicesOutput |
        Select-String -Pattern '^([^\s:]+)\s+device$' |
        ForEach-Object { $_.Matches[0].Groups[1].Value } |
        Select-Object -First 1
    if ($null -ne $usbSerial) {
        $wifiOutput = Invoke-AdbRead -Arguments @(
            '-s', $usbSerial, 'shell', 'ip', '-f', 'inet', 'addr', 'show', 'wlan0'
        )
        $wifiMatch = [regex]::Match(
            ($wifiOutput -join [Environment]::NewLine),
            '\binet\s+((?:\d{1,3}\.){3}\d{1,3})/'
        )
        if ($wifiMatch.Success) {
            $usbWifiIp = $wifiMatch.Groups[1].Value
        }
    }

    $connectedWifiSerials = $devicesOutput |
        Select-String -Pattern '^((?:\d{1,3}\.){3}\d{1,3}:\d{1,5})\s+device(?:\s|$)' |
        ForEach-Object { $_.Matches[0].Groups[1].Value } |
        Where-Object { $_ -notlike '*:5555' }
    if ($null -ne $usbWifiIp) {
        $wifiAddress = $connectedWifiSerials |
            Where-Object { $_ -like "${usbWifiIp}:*" } |
            Select-Object -First 1
    }
    if ($null -eq $wifiAddress) {
        $wifiAddress = $connectedWifiSerials | Select-Object -First 1
    }

    if ($null -eq $wifiAddress) {
        $mdnsMatches = @()
        for ($attempt = 0; $attempt -lt 3 -and $mdnsMatches.Count -eq 0; $attempt++) {
            if ($attempt -gt 0) {
                Start-Sleep -Seconds 1
            }
            $mdnsOutput = Invoke-AdbRead -Arguments @('mdns', 'services')
            $mdnsMatches = [regex]::Matches(
                ($mdnsOutput -join [Environment]::NewLine),
                '(?m)_adb-tls-connect\._tcp\s+((?:\d{1,3}\.){3}\d{1,3}:\d{1,5})\b'
            )
        }
        $mdnsAddresses = $mdnsMatches |
            ForEach-Object { $_.Groups[1].Value } |
            Where-Object { $_ -notlike '*:5555' }
        if ($null -ne $usbWifiIp) {
            $wifiAddress = $mdnsAddresses |
                Where-Object { $_ -like "${usbWifiIp}:*" } |
                Select-Object -First 1
        }
        if ($null -eq $wifiAddress) {
            $wifiAddress = $mdnsAddresses | Select-Object -First 1
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
