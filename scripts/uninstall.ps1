<#
.SYNOPSIS
  Removes Pollux for the current user. Settings (%AppData%\Pollux) and notes are kept unless -RemoveData.
#>
param([switch]$RemoveData)

$ErrorActionPreference = 'Continue'
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
Write-Host 'Pollux removed.'
