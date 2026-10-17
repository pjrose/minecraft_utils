[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Sender', 'Receiver')][string]$Role,
    [string]$ConfigPath,
    [string]$LogDirectory
)
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-PearlTrace ("diagnostic-" + $Role.ToLowerInvariant()) $LogDirectory
$ConfigPath = Get-ConfigPath ($Role.ToLowerInvariant()) $ConfigPath
Write-Host "Pearl Pull connection check: $Role"
Write-Host "Utility folder: $PSScriptRoot"
Write-Host "UTC time: $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host 'This check sends no mouse input and does not change any settings.'
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    Write-Host 'No saved pairing settings for this role. Run its normal launcher once to pair.'
    exit 1
}
try { $config = Read-Config $ConfigPath }
catch {
    # Do not print malformed JSON or exception details that could contain a pairing code.
    Write-Host 'Could not read valid pairing settings. Check the saved settings or repeat pairing.'
    exit 1
}
$hash = [System.Security.Cryptography.SHA256]::Create()
try { $digest = $hash.ComputeHash([PearlPull.Protocol]::ParseKey($config.PairingCode)) }
finally { $hash.Dispose() }
$fingerprint = [BitConverter]::ToString($digest).Replace('-', '').Substring(0, 12)
Write-Host "Pairing fingerprint: $fingerprint (must match on both PCs; this is not the pairing code)"
Write-Host "UDP port: $($config.Port)"

if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    try {
        Write-Host 'Network profiles:'
        Get-NetConnectionProfile -ErrorAction Stop |
            Select-Object InterfaceAlias, NetworkCategory | Format-Table -AutoSize | Out-Host
        Write-Host 'IPv4 addresses:'
        Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
            Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' } |
            Select-Object InterfaceAlias, IPAddress | Format-Table -AutoSize | Out-Host
    }
    catch { Write-Host 'Windows network details could not be read. Check them in Windows Settings.' }
    if ($Role -eq 'Receiver') {
        try {
            $listeners = @(Get-NetUDPEndpoint -LocalPort ([int]$config.Port) -ErrorAction Stop)
            Write-Host 'Processes listening on the configured UDP port:'
            $listeners | Select-Object LocalAddress, LocalPort, OwningProcess | Format-Table -AutoSize | Out-Host
        }
        catch { Write-Host 'No UDP listener was found, or Windows denied the query. Keep Start-Receiver.cmd open and look for the listening message.' }
        try {
            $rule = Get-NetFirewallRule -Name 'MinecraftUtils-PearlPull' -ErrorAction Stop
            Write-Host 'Pearl Pull firewall rule:'
            $rule | Select-Object Enabled, Profile, Direction, Action | Format-Table -AutoSize | Out-Host
            $rule | Get-NetFirewallPortFilter | Select-Object Protocol, LocalPort | Format-Table -AutoSize | Out-Host
            $rule | Get-NetFirewallAddressFilter | Select-Object RemoteAddress | Format-Table -AutoSize | Out-Host
        }
        catch { Write-Host 'The named Pearl Pull firewall rule was not found or could not be read. Follow the PC 2 firewall setup in README.md.' }
        Write-Host 'The supplied firewall rule applies to Private networks. Check that the home adapter uses Private.'
    }
}

if ($Role -eq 'Receiver') {
    $probe = [pscustomobject]@{ Address = '127.0.0.1'; Port = $config.Port; PairingCode = $config.PairingCode }
    Write-Host 'Testing the running receiver over local loopback...'
}
else {
    try {
        $address = [System.Net.IPAddress]::Parse($config.Address)
        if ($address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { throw 'IPv4 required' }
    }
    catch { Write-Host 'The sender configuration needs a valid IPv4 receiver address.'; exit 1 }
    $probe = $config
    Write-Host "Testing PC 2 at $($address.ToString())..."
}
$connection = Test-ReceiverConnection $probe
if ($connection.Ready) {
    Write-Host 'PASS: received an authenticated ready reply. No click was requested.'
    if ($Role -eq 'Receiver') {
        Write-Host 'Loopback proves the receiver and its saved pairing settings work here. It does not test the inbound firewall or connection from PC 1.'
    }
    exit 0
}
Write-Host 'FAIL: no authenticated ready reply. No click was requested.'
if ($Role -eq 'Receiver') {
    Write-Host 'Check that the receiver is running with these saved settings and listening on 0.0.0.0 or 127.0.0.1.'
}
else {
    Write-Host 'Run this check on PC 2 with -Role Receiver, then compare the IP, port, pairing fingerprint, and UTC time.'
    Write-Host 'If PC 2 passes locally, check its Private network/firewall rule and whether guest Wi-Fi or a VPN isolates the PCs.'
}
Write-Host 'Both clocks must be synchronized within five seconds. Windows Settings > Time & language > Date & time > Sync now.'
exit 1
