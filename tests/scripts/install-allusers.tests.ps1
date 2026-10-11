# Script-level checks for ticket 06 (S-09 part, ADR 0008). Run: pwsh -File tests/scripts/install-allusers.tests.ps1
# Windows-only behaviour (Program Files copy, shortcuts) is covered by the manual VM check.
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fail = 0
function Check([string]$name, [bool]$ok) { if ($ok) { Write-Host "PASS $name" } else { Write-Host "FAIL $name"; $script:fail++ } }

foreach ($f in 'install.ps1', 'uninstall.ps1') {
    $errs = $null
    [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root "scripts/$f"), [ref]$null, [ref]$errs) | Out-Null
    Check "$f parses" ($errs.Count -eq 0)
}

# Not elevated (always true on a Mac): -AllUsers must refuse before touching anything.
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('pollux-test-' + [guid]::NewGuid())
$src = Join-Path $tmp 'src'
New-Item -ItemType Directory $src | Out-Null
New-Item -ItemType File (Join-Path $src 'Pollux.exe') | Out-Null
$pf = Join-Path $tmp 'pf'
$env:ProgramFiles = $pf

$msg = ''
try { & (Join-Path $root 'scripts/install.ps1') -AllUsers -Source $src -NoStart } catch { $msg = $_.Exception.Message }
Check 'install -AllUsers requires elevation' ($msg -like '*elevat*')
Check 'install -AllUsers wrote nothing' (-not (Test-Path (Join-Path $pf 'Pollux')))

# A per-machine install exists, caller is not elevated: uninstall must say so and leave it alone.
New-Item -ItemType Directory (Join-Path $pf 'Pollux') -Force | Out-Null
New-Item -ItemType File (Join-Path $pf 'Pollux/Pollux.exe') | Out-Null
$msg = ''
try { & (Join-Path $root 'scripts/uninstall.ps1') -ErrorAction Stop } catch { $msg = $_.Exception.Message }
Check 'uninstall of per-machine install requires elevation' ($msg -like '*elevat*')
Check 'uninstall left the per-machine install in place' (Test-Path (Join-Path $pf 'Pollux/Pollux.exe'))

Remove-Item $tmp -Recurse -Force
exit $fail
