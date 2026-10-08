<#
.SYNOPSIS
  Installs Pollux for the current user. No admin rights needed.
.DESCRIPTION
  Copies the published app to %LocalAppData%\Programs\Pollux, creates a Start menu shortcut
  and (optionally) a desktop shortcut. Pollux registers its own autostart on first launch
  (Settings > Startup > Start with Windows).
.EXAMPLE
  .\install.ps1                       # installs from the folder this script is in (release zip)
  .\install.ps1 -Source ..\artifacts\win-x64
#>
param(
    [string]$Source = $PSScriptRoot,
    [switch]$DesktopShortcut,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Pollux'
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

New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'Pollux.lnk')
if ($DesktopShortcut) { New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Pollux.lnk') }

Write-Host 'Pollux installed. Start menu: Pollux.'
if (-not $NoStart) { Start-Process (Join-Path $target 'Pollux.exe') }
