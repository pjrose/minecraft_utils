[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$BindAddress = '0.0.0.0',
    [switch]$DryRun,
    [switch]$ShowPairingCode
)
. (Join-Path $PSScriptRoot 'Common.ps1')
if (-not $DryRun) { Assert-Windows }
$ConfigPath = Get-ConfigPath 'receiver' $ConfigPath
if (-not (Test-Path -LiteralPath $ConfigPath)) {
    $config = [ordered]@{ Port = 48123; PairingCode = [PearlPull.Protocol]::NewKey() }
    Save-Config $ConfigPath $config
    $ShowPairingCode = $true
}
$config = Read-Config $ConfigPath
if ($ShowPairingCode) {
    Write-Host "Pairing code for PC 1 (keep private): $($config.PairingCode)"
}
if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    Write-Host 'PC 2 network addresses (choose the home Ethernet/Wi-Fi address):'
    Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' } |
        Select-Object InterfaceAlias, IPAddress | Format-Table -AutoSize | Out-Host
}
Write-Host 'Keep this window open. Switch back to Minecraft and aim at the trapdoor.'
Write-Host 'Close this window to stop the receiver.'
[PearlPull.Transport]::Listen($BindAddress, [int]$config.Port, $config.PairingCode, [bool]$DryRun)
