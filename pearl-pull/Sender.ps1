[CmdletBinding()]
param(
    [string]$ConfigPath,
    [ValidateRange(1, 24)][int]$FunctionKey = 8,
    [switch]$Test,
    [switch]$Once
)
. (Join-Path $PSScriptRoot 'Common.ps1')
if ($Test -and $Once) { throw 'Use either -Test or -Once, not both.' }
if (-not $Test -and -not $Once) { Assert-Windows }
$ConfigPath = Get-ConfigPath 'sender' $ConfigPath
if (-not (Test-Path -LiteralPath $ConfigPath)) {
    $address = (Read-Host 'PC 2 home IPv4 address (shown by its receiver)').Trim()
    $parsed = [System.Net.IPAddress]::Parse($address)
    if ($parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { throw 'Enter an IPv4 address.' }
    $code = (Read-Host 'Pairing code shown on PC 2').Trim()
    [void][PearlPull.Protocol]::ParseKey($code)
    Save-Config $ConfigPath ([ordered]@{ Address = $address; Port = 48123; PairingCode = $code })
}
$config = Read-Config $ConfigPath
if ($Once) {
    $status = [PearlPull.Transport]::Send($config.Address, [int]$config.Port, $config.PairingCode, 'click')
    Write-Host $status
    if ($status -ne 'clicked' -and $status -ne 'dry-run') { exit 1 }
    exit 0
}
$connection = Test-ReceiverConnection $config
if ($connection.Ready) {
    Write-Host "Network check: ready (this does not click or check the game)."
}
elseif ($Test) {
    throw $connection.Message
}
else {
    Write-Warning "PC 2 did not answer the startup check. The hotkey will stay active; each press tries PC 2 again. $($connection.Message)"
}
if (-not $Test) {
    [PearlPull.DesktopInput]::RunHotkey($config.Address, [int]$config.Port, $config.PairingCode, $FunctionKey)
}
