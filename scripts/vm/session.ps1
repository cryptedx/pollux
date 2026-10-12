<#
.SYNOPSIS
  Runs one of the scripts next to this file inside the logged-on desktop session and waits for it.
.DESCRIPTION
  An ssh session has no desktop: GUI apps started from it are invisible and screenshots are black. A one-shot
  scheduled task with an Interactive principal runs in the user's desktop instead. The script runs headless
  (conhost --headless, no console window that would steal focus or show up in screenshots), writes its output to
  a log and its exit code to an .rc file; this wrapper prints the log and exits with that code.
  Usage (all arguments after the script name are passed on verbatim and must not contain spaces or quotes):
    session.ps1 <script.ps1> [-Name value ...]
#>
$ErrorActionPreference = 'Stop'
if ($args.Count -lt 1) { throw 'usage: session.ps1 <script.ps1> [arguments]' }
$script = Join-Path $PSScriptRoot $args[0]
$rest = @($args | Select-Object -Skip 1) -join ' '
$timeoutSec = 180

$id = [guid]::NewGuid().ToString('N').Substring(0, 8)
$name = "PolluxVm-$id"
$log = Join-Path $env:TEMP "$name.log"
$rc = Join-Path $env:TEMP "$name.rc"
$argument = "--headless powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`" -Log `"$log`" -Rc `"$rc`" $rest"

# Leftovers of runs that were interrupted.
Get-ScheduledTask -TaskName 'PolluxVm-*' -ErrorAction SilentlyContinue | Where-Object State -ne 'Running' | Unregister-ScheduledTask -Confirm:$false

$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument $argument
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Seconds $timeoutSec)
Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Force | Out-Null
try {
    # A one-shot task occasionally does not start at all (LastTaskResult 0x80070002); one retry covers it.
    for ($attempt = 1; $attempt -le 2 -and -not (Test-Path $rc); $attempt++) {
        Start-ScheduledTask -TaskName $name
        $deadline = (Get-Date).AddSeconds($timeoutSec)
        Start-Sleep -Milliseconds 500
        while ((Get-ScheduledTask -TaskName $name).State -eq 'Running' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
        for ($i = 0; $i -lt 10 -and -not (Test-Path $rc); $i++) { Start-Sleep -Milliseconds 200 }
    }
    if (Test-Path $log) { Get-Content $log | Write-Host }
    if (-not (Test-Path $rc)) {
        $info = Get-ScheduledTaskInfo -TaskName $name
        throw ("The task did not run in the desktop session (LastTaskResult 0x{0:X8}). Is {1} logged on to the VM desktop?" -f $info.LastTaskResult, $env:USERNAME)
    }
    $code = [int](Get-Content $rc -Raw).Trim()
} finally {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item $log, $rc -ErrorAction SilentlyContinue
}
exit $code
