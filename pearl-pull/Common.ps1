Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('PearlPull.Protocol' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'PearlPull.cs')
}

function Get-ConfigPath([string]$Role, [string]$Override) {
    if ($Override) { return $Override }
    if (-not $env:LOCALAPPDATA) { throw 'Use -ConfigPath when running a network-only test outside Windows.' }
    return Join-Path $env:LOCALAPPDATA "MinecraftUtils\pearl-pull-$Role.json"
}

function Save-Config([string]$Path, $Config) {
    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $Config | ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Read-Config([string]$Path) {
    $config = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    [void][PearlPull.Protocol]::ParseKey($config.PairingCode)
    if ([int]$config.Port -lt 1024 -or [int]$config.Port -gt 65535) { throw 'Port must be between 1024 and 65535.' }
    return $config
}

function Assert-Windows {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Mouse input and global hotkeys require Windows. Only -DryRun, -Test, and -Once networking checks work on other systems.'
    }
}

function Test-ReceiverConnection($Config) {
    try {
        $status = [PearlPull.Transport]::Send($Config.Address, [int]$Config.Port, $Config.PairingCode, 'ping')
        if ($status -ne 'ready') { throw "Unexpected receiver status: $status" }
        return [pscustomobject]@{ Ready = $true; Message = $status }
    }
    catch {
        return [pscustomobject]@{ Ready = $false; Message = $_.Exception.Message }
    }
}
