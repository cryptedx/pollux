<#
.SYNOPSIS
  Installs Pollux for the current user. No admin rights needed.
.DESCRIPTION
  With -AllUsers (needs an elevated PowerShell) Pollux goes to %ProgramFiles%\Pollux with an all-users
  Start menu shortcut instead, so the Admin Instance runs a binary only administrators can change
  (docs/adr/0008). Pollux is not started afterwards in this mode (it would inherit the elevation);
  start it from the Start menu. Remove either install with uninstall.ps1.
  Per-user: copies the published app to %LocalAppData%\Programs\Pollux, creates a Start menu shortcut
  and (optionally) a desktop shortcut. Pollux registers its own autostart on first launch
  (Settings > Startup > Start with Windows).
.EXAMPLE
  .\install.ps1                       # installs from the folder this script is in (release zip)
  .\install.ps1 -Source ..\artifacts\win-x64
  .\install.ps1 -AllUsers            # per-machine, run from an elevated PowerShell
#>
param(
    [string]$Source = $PSScriptRoot,
    [switch]$DesktopShortcut,
    [switch]$NoStart,
    [switch]$AllUsers
)

$ErrorActionPreference = 'Stop'
if ($AllUsers) {
    $isAdmin = $false
    try { $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch { }
    if (-not $isAdmin) { throw '-AllUsers requires an elevated PowerShell (Run as administrator).' }
    $target = Join-Path $env:ProgramFiles 'Pollux'
    $startMenu = [Environment]::GetFolderPath('CommonPrograms')
} else {
    $target = Join-Path $env:LOCALAPPDATA 'Programs\Pollux'
    $startMenu = [Environment]::GetFolderPath('Programs')
}
$exe = Join-Path $Source 'Pollux.exe'
if (-not (Test-Path $exe)) { throw "Pollux.exe not found in $Source. Pass -Source <publish folder>." }

# Stop a running Pollux (resident tray process) so files can be replaced.
$running = Get-Process Pollux -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" }
if ($running) {
    Write-Host 'Stopping running Pollux...'
    & (Join-Path $target 'Pollux.exe') --quit 2>$null
    Start-Sleep -Milliseconds 800
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force $target | Out-Null
Write-Host "Copying to $target..."
robocopy $Source $target /MIR /NFL /NDL /NJH /NJS /NP /XF install.ps1 uninstall.ps1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copy failed (robocopy exit $LASTEXITCODE)" }
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $target -Force -ErrorAction SilentlyContinue

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$Path) {
    $s = $shell.CreateShortcut($Path)
    $s.TargetPath = Join-Path $target 'Pollux.exe'
    $s.WorkingDirectory = $target
    $s.IconLocation = (Join-Path $target 'Pollux.exe') + ',0'
    $s.Description = 'Pollux file manager'
    $s.Save()
}

New-Shortcut (Join-Path $startMenu 'Pollux.lnk')
if ($DesktopShortcut) {
    $desktop = if ($AllUsers) { [Environment]::GetFolderPath('CommonDesktopDirectory') } else { [Environment]::GetFolderPath('Desktop') }
    New-Shortcut (Join-Path $desktop 'Pollux.lnk')
}

Write-Host 'Pollux installed. Start menu: Pollux.'
if ($AllUsers) { Write-Host 'Per-machine install: start Pollux from the Start menu (not from this elevated shell).' }
elseif (-not $NoStart) { Start-Process (Join-Path $target 'Pollux.exe') }
