<#
.SYNOPSIS
  Removes Pollux: the per-user install and, if present, the per-machine install (%ProgramFiles%\Pollux,
  needs an elevated PowerShell; without elevation it is left in place with a warning). Settings (%AppData%\Pollux)
  and notes are kept unless -RemoveData.
#>
param([switch]$RemoveData)

$ErrorActionPreference = 'Continue'

# Per-machine install (install.ps1 -AllUsers): only an elevated PowerShell can remove it. Without elevation it is
# skipped with a warning and the per-user install below is still removed.
$machineTarget = Join-Path $env:ProgramFiles 'Pollux'
$hasMachine = Test-Path (Join-Path $machineTarget 'Pollux.exe')
$isAdmin = $false
if ($hasMachine) {
    try { $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch { }
    if (-not $isAdmin) { Write-Warning "Pollux is also installed for all users in $machineTarget. Removing that install requires an elevated PowerShell (Run as administrator); it is left in place." }
}
if ($hasMachine -and $isAdmin) {
    & (Join-Path $machineTarget 'Pollux.exe') --quit 2>$null; Start-Sleep -Milliseconds 800
    Get-Process Pollux -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$machineTarget*" } | Stop-Process -Force
    Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Pollux.lnk') -ErrorAction SilentlyContinue
    Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Pollux.lnk') -ErrorAction SilentlyContinue
    Remove-Item $machineTarget -Recurse -Force -ErrorAction SilentlyContinue
}

$target = Join-Path $env:LOCALAPPDATA 'Programs\Pollux'
if (Test-Path (Join-Path $target 'Pollux.exe')) { & (Join-Path $target 'Pollux.exe') --quit 2>$null; Start-Sleep -Milliseconds 800 }
Get-Process Pollux -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force

# Win+V takeover: put Windows' clipboard history switch back to what it was (also covers a force-killed Pollux).
$winV = Join-Path $env:LOCALAPPDATA 'Pollux\clipboard\winv-restore.json'
if (Test-Path $winV) {
    $clipKey = 'HKCU:\Software\Microsoft\Clipboard'
    $previous = (Get-Content $winV -Raw).Trim()
    if ($previous -match '^\d+$') {
        if (-not (Test-Path $clipKey)) { New-Item -Path $clipKey -Force | Out-Null }
        Set-ItemProperty -Path $clipKey -Name 'EnableClipboardHistory' -Value ([int]$previous) -Type DWord
        Remove-Item $winV -ErrorAction SilentlyContinue
    } elseif ($previous -ceq 'absent') {
        Remove-ItemProperty -Path $clipKey -Name 'EnableClipboardHistory' -ErrorAction SilentlyContinue
        Remove-Item $winV -ErrorAction SilentlyContinue
    } else {
        # Unrecognized state: leave the Windows setting alone and keep the file, the only record of the previous value.
        Write-Warning "Unrecognized Win+V restore state in $winV; Windows clipboard history setting left unchanged and the file kept."
    }
}

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Pollux' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Pollux.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Pollux.lnk') -ErrorAction SilentlyContinue
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) {
    Remove-Item (Join-Path $env:APPDATA 'Pollux') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA 'Pollux') -Recurse -Force -ErrorAction SilentlyContinue
}
if ($hasMachine -and -not $isAdmin) { Write-Host 'Pollux removed for this user; the all-users install is still there.' }
else { Write-Host 'Pollux removed.' }
