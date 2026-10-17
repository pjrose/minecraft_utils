[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Sender', 'Receiver')][string]$Role,
    [ValidateSet('Install', 'Start', 'Stop', 'Status', 'Remove')][string]$Action = 'Install',
    [ValidateRange(1, 24)][int]$FunctionKey = 8
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Task management does not load the input helper: Stop/Remove remain independent
# of whether the application can compile or the pairing file is still present.
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Startup tasks require Windows Task Scheduler.'
}
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$sid = $identity.User.Value
$taskName = "MinecraftUtils-PearlPull-$Role-$sid"
$description = "MinecraftUtils Pearl Pull $Role for $sid. Managed by Manage-Startup.ps1."
$existing = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
if ($existing -and $existing.Description -ne $description) {
    throw "The task name $taskName is already in use by another task. It has not been changed."
}

if ($Action -eq 'Status') {
    if (-not $existing) { Write-Host "$Role startup is not installed for this Windows user."; return }
    $existing | Select-Object TaskName, State | Format-Table -AutoSize | Out-Host
    Get-ScheduledTaskInfo -TaskName $taskName -TaskPath '\' |
        Select-Object LastRunTime, LastTaskResult, NextRunTime | Format-List | Out-Host
    return
}
if ($Action -eq 'Stop' -or $Action -eq 'Remove') {
    if ($existing) {
        Stop-ScheduledTask -TaskName $taskName -TaskPath '\'
        if ($Action -eq 'Remove') {
            Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false
        }
    }
    Write-Host "$Role startup task: $Action complete. Manually launched windows are separate."
    return
}
if ($Action -eq 'Start') {
    if (-not $existing) { throw 'Install the startup task first.' }
    Start-ScheduledTask -TaskName $taskName -TaskPath '\'
    Write-Host "$Role start requested. Use -Action Status or inspect the minimized window to check it."
    return
}

$configPath = Join-Path $env:LOCALAPPDATA "MinecraftUtils\pearl-pull-$($Role.ToLowerInvariant()).json"
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Complete $Role pairing with its normal launcher before installing startup."
}
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.PairingCode -notmatch '\A[0-9a-fA-F]{64}\z' -or [int]$config.Port -lt 1024 -or [int]$config.Port -gt 65535) {
    throw 'The saved pairing configuration is invalid. Run the normal launcher to fix it first.'
}
if ($Role -eq 'Sender') {
    $address = [System.Net.IPAddress]::Parse($config.Address)
    if ($address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'The sender needs an IPv4 receiver address.'
    }
}
$scriptPath = Join-Path $PSScriptRoot "$Role.ps1"
foreach ($path in @($scriptPath, (Join-Path $PSScriptRoot 'Common.ps1'), (Join-Path $PSScriptRoot 'PearlPull.cs'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required application file is missing: $path" }
}
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$arguments = '-NoLogo -NoProfile -NonInteractive -WindowStyle Minimized -ExecutionPolicy RemoteSigned -File "' + $scriptPath + '"'
if ($Role -eq 'Sender') { $arguments += " -FunctionKey $FunctionKey" }
$taskAction = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $sid
$principal = New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

if ($existing) { Stop-ScheduledTask -TaskName $taskName -TaskPath '\' }
Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Action $taskAction -Trigger $trigger `
    -Principal $principal -Settings $settings -Description $description -Force | Out-Null
Start-ScheduledTask -TaskName $taskName -TaskPath '\'
Write-Host "$Role startup installed for $($identity.Name); a start was requested now."
Write-Host 'It runs minimized at sign-in, with normal user permissions. Windows security policy still applies.'
Write-Host 'Use -Action Status to check the task, -Action Stop to stop it, or -Action Remove to uninstall startup.'
Write-Host 'Close any manually launched copy to avoid a duplicate port/hotkey conflict.'
