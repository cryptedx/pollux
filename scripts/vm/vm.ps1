<#
.SYNOPSIS
  VM side of scripts/vm.sh: builds, tests and removes throwaway Pollux clones under -Root. Runs over ssh, outside
  the desktop session (session.ps1 covers what needs the desktop).
.DESCRIPTION
  Layout: <Root>\<slug> is one clone (or an unpacked release), its app in <Root>\<slug>\artifacts\win-arm64;
  <Root>\current.txt names the slug the other commands use by default; <Root>\.tools holds these scripts.
  Only processes whose executable lies under <Root> are ever stopped.
#>
param(
    [Parameter(Mandatory)] [ValidateSet('build', 'release', 'test', 'stop', 'status', 'clean')] [string]$Action,
    [Parameter(Mandatory)] [string]$Root,
    [string]$Slug,
    [string]$Bundle,
    [string]$Ref,
    [string]$Tag,
    [string]$Repo = 'cryptedx/pollux',
    [string[]]$Project,
    [switch]$All
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Build servers left running would keep files in the clone locked, so clean could not delete it.
# Environment, not -nodeReuse:false on the command line: dotnet test hands unknown arguments to the test app.
$env:MSBUILDDISABLENODEREUSE = '1'
$env:UseSharedCompilation = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'

$currentFile = Join-Path $Root 'current.txt'

function Resolve-Slug {
    if ($Slug) { return $Slug }
    if (Test-Path $currentFile) { return (Get-Content $currentFile -Raw).Trim() }
    throw "No clone yet. Run: scripts/vm.sh install <ref>"
}

function Get-CloneProcesses([string]$Dir) {
    $prefix = (Join-Path $Dir '').ToLowerInvariant()
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.ToLowerInvariant().StartsWith($prefix) }
}

function Stop-Clone([string]$Dir) {
    $procs = @(Get-CloneProcesses $Dir)
    foreach ($p in $procs) {
        Write-Host "Stopping $($p.ProcessName) (pid $($p.Id)) $($p.Path)"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    if ($procs) { Start-Sleep -Milliseconds 800 }
}

function Invoke-Logged([string]$Log, [scriptblock]$Block) {
    # Windows PowerShell turns redirected native stderr into error records; with 'Stop' the first one would throw.
    $ErrorActionPreference = 'Continue'
    & $Block *>> $Log
    if ($LASTEXITCODE -ne 0) {
        Get-Content $Log -Tail 40 | Write-Host
        throw "failed (exit $LASTEXITCODE), full log: $Log"
    }
}

New-Item -ItemType Directory -Force $Root | Out-Null

switch ($Action) {
    'build' {
        $dir = Join-Path $Root $Slug
        $log = Join-Path $Root "$Slug.build.log"
        Set-Content $log "build $Ref $(Get-Date -Format s)"
        Stop-Clone $dir
        New-Item -ItemType Directory -Force $dir | Out-Null
        Push-Location $dir
        try {
            if (-not (Test-Path .git)) { Invoke-Logged $log { git init --quiet } }
            # The ref is named explicitly: git clone would skip refs/remotes/* in a bundle.
            Invoke-Logged $log { git fetch --quiet --force $Bundle $Ref }
            Invoke-Logged $log { git -c advice.detachedHead=false checkout --quiet --force --detach FETCH_HEAD }
            $sha = (git rev-parse --short HEAD).Trim()
            Write-Host "Checked out $Ref ($sha) in $dir; publishing ARM64 Release (log: $log)..."
            $sw = [Diagnostics.Stopwatch]::StartNew()
            # Same command as release.yml; the RID follows from Platform.
            Invoke-Logged $log { dotnet publish src\Pollux.App -c Release -p:Platform=ARM64 -o artifacts\win-arm64 }
            Write-Host ("Built {0} in {1:n0} s: {2}" -f $sha, $sw.Elapsed.TotalSeconds, (Join-Path $dir 'artifacts\win-arm64\Pollux.exe'))
        } finally {
            Pop-Location
            Remove-Item $Bundle -ErrorAction SilentlyContinue
        }
        Set-Content $currentFile $Slug
    }
    'release' {
        $dir = Join-Path $Root $Slug
        $out = Join-Path $dir 'artifacts\win-arm64'
        Stop-Clone $dir
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        New-Item -ItemType Directory -Force $out | Out-Null
        $zip = Join-Path $dir 'Pollux-win-arm64.zip'
        $sums = Join-Path $dir 'SHA256SUMS'
        $base = "https://github.com/$Repo/releases/download/$Tag"
        Write-Host "Downloading $base/Pollux-win-arm64.zip ..."
        # curl.exe: Invoke-WebRequest takes minutes for the same file.
        curl.exe -fsSL -o $zip "$base/Pollux-win-arm64.zip"
        if ($LASTEXITCODE -ne 0) { throw "download failed (curl exit $LASTEXITCODE)" }
        curl.exe -fsSL -o $sums "$base/SHA256SUMS"
        if ($LASTEXITCODE -ne 0) { throw "SHA256SUMS download failed (curl exit $LASTEXITCODE)" }
        $expected = (Get-Content $sums | Where-Object { $_ -match '\sPollux-win-arm64\.zip$' } | ForEach-Object { ($_ -split '\s+')[0] }) | Select-Object -First 1
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
        if (-not $expected -or $actual -ne $expected.ToUpperInvariant()) { throw "SHA256 mismatch: expected $expected, got $actual" }
        Write-Host "SHA256 ok ($actual)"
        Expand-Archive $zip -DestinationPath $out -Force
        Set-Content $currentFile $Slug
        Write-Host "Unpacked $Tag to $out"
    }
    'test' {
        $dir = Join-Path $Root (Resolve-Slug)
        if (-not (Test-Path (Join-Path $dir '.git'))) { throw "$dir is not a source clone (release builds have no tests)" }
        # Comes in as one comma-separated string over ssh.
        $Project = @($Project | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
        if (-not $Project) { $Project = 'tests\Pollux.Core.Tests', 'tests\Pollux.Copilot.Tests' }
        if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Write-Warning 'node is not on PATH: the fake-CLI Copilot tests will be inconclusive. Run: scripts/vm.sh setup' }
        Push-Location $dir
        $failed = 0
        try {
            foreach ($p in $Project) {
                Write-Host "== dotnet test --project $p"
                dotnet test --project $p -c Release
                if ($LASTEXITCODE -ne 0) { $failed++ }
            }
        } finally { Pop-Location }
        if ($failed) { exit 1 }
    }
    'stop' {
        Stop-Clone (Join-Path $Root (Resolve-Slug))
    }
    'status' {
        $current = if (Test-Path $currentFile) { (Get-Content $currentFile -Raw).Trim() } else { '' }
        Get-ChildItem $Root -Directory | Where-Object Name -ne '.tools' | ForEach-Object {
            $sha = if (Test-Path (Join-Path $_.FullName '.git')) { (git -C $_.FullName rev-parse --short HEAD 2>$null) } else { 'release' }
            $mark = if ($_.Name -eq $current) { '*' } else { ' ' }
            $running = @(Get-CloneProcesses $_.FullName | ForEach-Object { "$($_.ProcessName):$($_.Id)" }) -join ' '
            '{0} {1,-28} {2,-9} {3}' -f $mark, $_.Name, $sha, $running
        }
        $others = Get-Process Pollux -ErrorAction SilentlyContinue | Where-Object { $_.Path -and -not $_.Path.ToLowerInvariant().StartsWith((Join-Path $Root '').ToLowerInvariant()) }
        foreach ($p in $others) { "  (not ours) Pollux pid $($p.Id) $($p.Path)" }
    }
    'clean' {
        $targets = if ($All) { Get-ChildItem $Root -Directory | Where-Object Name -ne '.tools' } else { Get-Item (Join-Path $Root (Resolve-Slug)) -ErrorAction SilentlyContinue }
        foreach ($t in @($targets)) {
            if (-not $t) { continue }
            Stop-Clone $t.FullName
            Write-Host "Removing $($t.FullName)"
            Remove-Item $t.FullName -Recurse -Force
            Remove-Item (Join-Path $Root "$($t.Name).build.log") -ErrorAction SilentlyContinue
            if ((Test-Path $currentFile) -and (Get-Content $currentFile -Raw).Trim() -eq $t.Name) { Remove-Item $currentFile }
        }
    }
}
