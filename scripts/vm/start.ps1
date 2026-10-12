<#
.SYNOPSIS
  In the desktop session (via session.ps1): starts the Pollux of one clone, or hands commands to it.
.DESCRIPTION
  -Restart stops the Pollux processes of every clone under the same root and starts this clone's with remote
  debugging on -Port, so scripts/cdp.mjs can reach its WebView2 pages. Without -Restart the command ids go to the
  running instance the way a second launch does.
  The clone runs with its own profile (APPDATA/LOCALAPPDATA under <clone>\profile, seeded once from the real
  %APPDATA%\Pollux) so it never writes the settings, workspaces or logs of an installed Pollux. -SharedProfile
  uses the real profile instead.
#>
param(
    [Parameter(Mandatory)] [string]$Log,
    [Parameter(Mandatory)] [string]$Rc,
    [Parameter(Mandatory)] [string]$Dir,
    [string]$Commands,
    [int]$Port = 9222,
    [switch]$Restart,
    [switch]$SharedProfile
)

$code = 1
function Say([string]$Message) { Add-Content -Path $Log -Value $Message }
try {
    $ErrorActionPreference = 'Stop'
    $exe = Join-Path $Dir 'artifacts\win-arm64\Pollux.exe'
    if (-not (Test-Path $exe)) { throw "$exe not found. Run: scripts/vm.sh install <ref>" }
    $prefix = (Join-Path $Dir '').ToLowerInvariant()
    $ours = { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.ToLowerInvariant().StartsWith($prefix) }) }
    $pollux = { @(& $ours | Where-Object ProcessName -eq 'Pollux') }

    $argList = @()
    if ($Commands) { $argList += '--command', $Commands }

    if (-not $Restart) {
        if (-not (& $pollux)) { throw 'The clone''s Pollux is not running. Run: scripts/vm.sh run' }
        Start-Process $exe -ArgumentList $argList -Wait
        Say "Sent --command $Commands"
        $code = 0
        return
    }

    # Every clone under the root, not only this one: they would all ask WebView2 for the same DevTools port.
    $rootPrefix = (Join-Path (Split-Path $Dir) '').ToLowerInvariant()
    $allClones = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.ToLowerInvariant().StartsWith($rootPrefix) })
    foreach ($p in $allClones) { Say "Stopping $($p.ProcessName) (pid $($p.Id))"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500

    $others = @(Get-Process Pollux -ErrorAction SilentlyContinue | Where-Object { $_.Path -and -not $_.Path.ToLowerInvariant().StartsWith($rootPrefix) })
    foreach ($p in $others) { Say "Note: another Pollux runs too (pid $($p.Id), $($p.Path)); it is left alone." }

    if (-not $SharedProfile) {
        $profileDir = Join-Path $Dir 'profile'
        $roaming = Join-Path $profileDir 'Roaming'
        $local = Join-Path $profileDir 'Local'
        if (-not (Test-Path $roaming)) {
            New-Item -ItemType Directory -Force $roaming, $local | Out-Null
            $real = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Pollux'
            if (Test-Path $real) { Copy-Item $real $roaming -Recurse; Say "Seeded profile from $real" }
        }
        $env:APPDATA = $roaming
        $env:LOCALAPPDATA = $local
        Say "Profile: $profileDir"
    }
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port"

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $proc = if ($argList) { Start-Process $exe -ArgumentList $argList -PassThru } else { Start-Process $exe -PassThru }
    while ($sw.Elapsed.TotalSeconds -lt 30) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh()
        if ($proc.HasExited) {
            throw "Pollux exited right away (code $($proc.ExitCode)). With code 0 it handed its command line to another running Pollux (single-instance key)."
        }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "Pollux (pid $($proc.Id)) shows no window after 30 s." }
    Say ("Pollux pid {0} window after {1:n1} s; DevTools on 127.0.0.1:{2} in the VM" -f $proc.Id, $sw.Elapsed.TotalSeconds, $Port)
    $code = 0
} catch {
    Say "ERROR: $($_.Exception.Message)"
} finally {
    Set-Content $Rc $code
}
