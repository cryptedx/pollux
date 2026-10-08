<#
.SYNOPSIS
  Removes Pollux for the current user. Settings (%AppData%\Pollux) and notes are kept unless -RemoveData.
#>
param([switch]$RemoveData)

$ErrorActionPreference = 'Continue'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Pollux'
if (Test-Path (Join-Path $target 'Pollux.exe')) { & (Join-Path $target 'Pollux.exe') --quit 2>$null; Start-Sleep -Milliseconds 800 }
Get-Process Pollux -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Pollux' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Pollux.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Pollux.lnk') -ErrorAction SilentlyContinue
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) {
    Remove-Item (Join-Path $env:APPDATA 'Pollux') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA 'Pollux') -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Pollux removed.'
