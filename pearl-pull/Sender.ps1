[CmdletBinding()]
param(
    [string]$ConfigPath,
    [ValidateRange(1, 24)][int]$FunctionKey = 8,
    [ValidateRange(1024, 65535)][int]$StreamDeckPort = 48124,
    [string]$LogDirectory,
    [switch]$HotkeyTest,
    [switch]$StreamDeckOnly,
    [switch]$ShowStreamDeckUrl,
    [switch]$Test,
    [switch]$Once
)
. (Join-Path $PSScriptRoot 'Common.ps1')
if ($HotkeyTest) { Initialize-PearlTrace 'hotkey-test' $LogDirectory }
else { Initialize-PearlTrace 'sender' $LogDirectory }
function Start-PearlSender {
    $modes = @($HotkeyTest, $Test, $Once, $StreamDeckOnly, $ShowStreamDeckUrl) | Where-Object { $_ }
    if (@($modes).Count -gt 1) { throw 'Choose only one of -HotkeyTest, -Test, -Once, -StreamDeckOnly, or -ShowStreamDeckUrl.' }
    if (-not $Test -and -not $Once -and -not $StreamDeckOnly -and -not $ShowStreamDeckUrl) { Assert-Windows }
    [PearlPull.TraceLog]::Write('INFO', 'sender.mode', "pc=PC1 role=client key=F$FunctionKey hotkey_test=$HotkeyTest network_test=$Test one_shot=$Once streamdeck_only=$StreamDeckOnly")
    if ($HotkeyTest) {
        [PearlPull.DesktopInput]::RunHotkey('', 0, '', $FunctionKey, $true)
        return
    }
    $ConfigPath = Get-ConfigPath 'sender' $ConfigPath
    if (-not (Test-Path -LiteralPath $ConfigPath)) {
        [PearlPull.TraceLog]::Write('INFO', 'pairing.required', 'role=sender interactive_prompt=true code_will_not_be_logged=true')
        $address = (Read-Host 'PC 2 home IPv4 address (shown by its receiver)').Trim()
        $parsed = [System.Net.IPAddress]::Parse($address)
        if ($parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { throw 'Enter an IPv4 address.' }
        $code = (Read-Host 'Pairing code shown on PC 2').Trim()
        [void][PearlPull.Protocol]::ParseKey($code)
        Save-Config $ConfigPath ([ordered]@{ Address = $address; Port = 48123; PairingCode = $code })
    }
    $config = Read-Config $ConfigPath
    if ($ShowStreamDeckUrl) {
        Initialize-StreamDeckConfig $ConfigPath $config
        Write-Host 'On PC 1, set the Neo button to System > Website and enable GET request in background.'
        Write-Host 'Copy this local trigger URL into that action. Keep it private; it is not included in the log file:'
        Write-Host "http://127.0.0.1:$StreamDeckPort/pull/$($config.StreamDeckToken)"
        Write-Host 'Then keep Start-StreamDeck-Neo.cmd running on PC 1.'
        return
    }
    [PearlPull.TraceLog]::Write('INFO', 'sender.destination', "pc=PC2 ipv4=$($config.Address) port=$($config.Port)")
    Write-NetworkTrace $false
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
        [PearlPull.TraceLog]::Write('WARN', 'sender.startup_probe_failed', 'hotkey_will_remain_available=true check_receiver_log=true')
        Write-Warning "PC 2 did not answer the startup check. The hotkey will stay active; each press tries PC 2 again. $($connection.Message)"
    }
    if (-not $Test) {
        Initialize-StreamDeckConfig $ConfigPath $config
        $trigger = [PearlPull.StreamDeckTrigger]::new($config.Address, [int]$config.Port, $config.PairingCode, $config.StreamDeckToken, $StreamDeckPort)
        try {
            $trigger.Start()
            Write-Host 'Neo direct trigger is running on PC 1. Use Setup-StreamDeck.cmd to display its URL.'
            if ($StreamDeckOnly) { $trigger.Wait() }
            else { [PearlPull.DesktopInput]::RunHotkey($config.Address, [int]$config.Port, $config.PairingCode, $FunctionKey) }
        }
        finally { $trigger.Dispose() }
    }
}
try { Start-PearlSender }
catch { [PearlPull.TraceLog]::Failure('app.failed', $_.Exception, 'script=Sender.ps1'); throw }
finally { [PearlPull.TraceLog]::Write('INFO', 'app.stopped', 'script=Sender.ps1'); [PearlPull.TraceLog]::Close() }
