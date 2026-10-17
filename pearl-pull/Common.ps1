Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('PearlPull.Protocol' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'PearlPull.cs')
}

function Initialize-PearlTrace([string]$Role, [string]$Directory) {
    if (-not $Directory) {
        if ($env:LOCALAPPDATA) { $Directory = Join-Path $env:LOCALAPPDATA 'MinecraftUtils\logs' }
        else { $Directory = Join-Path ([System.IO.Path]::GetTempPath()) 'MinecraftUtils-logs' }
    }
    [PearlPull.TraceLog]::Initialize($Role, $Directory)
    [PearlPull.TraceLog]::Write('INFO', 'runtime.details', "powershell=$($PSVersionTable.PSVersion) language_mode=$($ExecutionContext.SessionState.LanguageMode) folder=$PSScriptRoot")
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

function Initialize-StreamDeckConfig([string]$Path, $Config) {
    if (-not $Config.PSObject.Properties['StreamDeckToken']) {
        $Config | Add-Member -NotePropertyName StreamDeckToken -NotePropertyValue ([PearlPull.Protocol]::NewKey())
        Save-Config $Path $Config
    }
    [void][PearlPull.Protocol]::ParseKey($Config.StreamDeckToken)
}

function Read-Config([string]$Path) {
    $config = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    [void][PearlPull.Protocol]::ParseKey($config.PairingCode)
    if ([int]$config.Port -lt 1024 -or [int]$config.Port -gt 65535) { throw 'Port must be between 1024 and 65535.' }
    [PearlPull.TraceLog]::Write('INFO', 'config.loaded', "path=$Path port=$($config.Port) pairing_fingerprint=$([PearlPull.Protocol]::Fingerprint($config.PairingCode))")
    return $config
}

function Assert-Windows {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Mouse input and global hotkeys require Windows. Network-only modes work on other systems.'
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

function Write-NetworkTrace([bool]$Receiver) {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return }
    try {
        foreach ($profile in @(Get-NetConnectionProfile -ErrorAction Stop)) {
            [PearlPull.TraceLog]::Write('INFO', 'network.profile', "interface=$($profile.InterfaceAlias) category=$($profile.NetworkCategory)")
        }
        foreach ($address in @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop)) {
            if ($address.IPAddress -ne '127.0.0.1' -and $address.IPAddress -notlike '169.254.*') {
                [PearlPull.TraceLog]::Write('INFO', 'network.address', "interface=$($address.InterfaceAlias) ipv4=$($address.IPAddress) prefix=$($address.PrefixLength)")
            }
        }
    }
    catch { [PearlPull.TraceLog]::Failure('network.inspect_failed', $_.Exception, 'startup_continues=true') }
    if ($Receiver) {
        try {
            $rule = Get-NetFirewallRule -Name 'MinecraftUtils-PearlPull' -ErrorAction Stop
            [PearlPull.TraceLog]::Write('INFO', 'firewall.rule', "enabled=$($rule.Enabled) profile=$($rule.Profile) direction=$($rule.Direction) action=$($rule.Action)")
            $ports = $rule | Get-NetFirewallPortFilter
            [PearlPull.TraceLog]::Write('INFO', 'firewall.ports', "protocol=$($ports.Protocol) local_port=$($ports.LocalPort)")
        }
        catch { [PearlPull.TraceLog]::Write('WARN', 'firewall.rule_unavailable', 'named_rule_missing_or_unreadable=true expected=Private_inbound_UDP_48123 startup_continues=true') }
    }
}
