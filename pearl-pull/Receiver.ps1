[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$BindAddress = '0.0.0.0',
    [string]$LogDirectory,
    [switch]$DryRun,
    [switch]$ShowPairingCode
)
. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-PearlTrace 'receiver' $LogDirectory
function Start-PearlReceiver {
    if (-not $DryRun) { Assert-Windows }
    [PearlPull.TraceLog]::Write('INFO', 'receiver.mode', "pc=PC2 role=server bind=$BindAddress dry_run=$DryRun listens_for_F8=false")
    $ConfigPath = Get-ConfigPath 'receiver' $ConfigPath
    if (-not (Test-Path -LiteralPath $ConfigPath)) {
        [PearlPull.TraceLog]::Write('INFO', 'pairing.created', 'role=receiver code_will_not_be_logged=true')
        $config = [ordered]@{ Port = 48123; PairingCode = [PearlPull.Protocol]::NewKey() }
        Save-Config $ConfigPath $config
        $ShowPairingCode = $true
    }
    $config = Read-Config $ConfigPath
    if ($ShowPairingCode) {
        Write-Host "Pairing code for PC 1 (keep private): $($config.PairingCode)"
    }
    Write-NetworkTrace $true
    Write-Host 'PC 2 RECEIVER: waits for network requests. F8 must be detected by the SENDER on PC 1.'
    Write-Host 'Keep this window open. Switch back to Minecraft and aim at the trapdoor.'
    Write-Host 'Close this window to stop the receiver.'
    [PearlPull.Transport]::Listen($BindAddress, [int]$config.Port, $config.PairingCode, [bool]$DryRun)
}
try { Start-PearlReceiver }
catch { [PearlPull.TraceLog]::Failure('app.failed', $_.Exception, 'script=Receiver.ps1'); throw }
finally { [PearlPull.TraceLog]::Write('INFO', 'app.stopped', 'script=Receiver.ps1'); [PearlPull.TraceLog]::Close() }
