[CmdletBinding()]
param([string]$Makensis = 'makensis', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$optionPrefix = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { '/' } else { '-' }
foreach ($role in @('Button', 'Receiver')) {
    $app = Join-Path $OutputDirectory $role
    & dotnet build (Join-Path $root 'pearl-pull/tray/PearlPull.Tray.csproj') -c Release "-p:AppRole=$role" -o $app --nologo
    if ($LASTEXITCODE -ne 0) { throw "The $role build failed." }
    New-Item -ItemType Directory -Path (Join-Path $app 'icons') -Force | Out-Null
    Copy-Item (Join-Path $root 'pearl-pull/icons/pearl-pull.png') (Join-Path $app 'icons/pearl-pull.png')
    Copy-Item (Join-Path $PSScriptRoot 'START-HERE.txt') $app
    & $Makensis "$($optionPrefix)WX" "$($optionPrefix)DROLE=$role" "$($optionPrefix)DAPPDIR=$app" "$($optionPrefix)DOUTDIR=$OutputDirectory" (Join-Path $PSScriptRoot 'PearlPull.nsi')
    if ($LASTEXITCODE -ne 0) { throw "The $role installer build failed." }
}
Copy-Item (Join-Path $PSScriptRoot 'START-HERE.txt') $OutputDirectory
$hashes = Get-ChildItem $OutputDirectory -Filter '*-Setup.exe' | Get-FileHash -Algorithm SHA256
$hashes | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } |
    Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII
$hashes
