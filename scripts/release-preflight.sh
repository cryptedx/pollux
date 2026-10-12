#!/usr/bin/env bash
# Release preflight: checks a commit the way the public GitHub copy will see it, before it is tagged.
# usage: scripts/release-preflight.sh [--no-tests] [<ref>]   (default HEAD; committed state only)
#
# Releases 1.5.1 to 1.5.3 were tagged on Forgejo and failed only in GitHub's release.yml. This script repeats what
# can be repeated off Windows, on a throwaway export of <ref> (never in the checkout itself):
#   1. export <ref> and drop what publish-github drops (its default.publicignore plus .publicignore);
#   2. language: the published Markdown (top-level *.md, docs/) is English prose; code spans are exempt;
#   3. RID: for each platform in release.yml's matrix, Pollux.App evaluated with release.yml's publish flags must
#      target the matrix's rid (1.5.0 to 1.5.2: no RID from Platform, ARM64 failed with NETSDK1032);
#   4. locked restore: Pollux.App and its references restore with those flags against the committed lock files;
#   5. tests: Pollux.Core.Tests and Pollux.Copilot.Tests pass on the export (1.5.1: a test read .forgejo/, which
#      the public copy does not have).
# Not checkable here: the Windows-only publish itself (WinUI, Native AOT) and GitHub-side features such as build
# provenance attestations, which a private GitHub repo does not offer (1.5.3).
#
# Locked restore needs the official SDK build. CI=true (the Forgejo runner, like GitHub) enforces it fully; off CI a
# source-built SDK (Homebrew) fails the content hash of the packages it ships itself (ILLink, ILCompiler, see
# Directory.Build.targets), so only those NU1403 errors are tolerated there, with a warning. Tests run with the
# caller's CI setting. Needs git, dotnet (global.json's SDK) and node (Copilot tests). Exits 1 on any failure.
set -uo pipefail

RUN_TESTS=true
REF=HEAD
for arg in "$@"; do
  case $arg in
    --no-tests) RUN_TESTS=false ;;
    -h|--help) sed -n '2,3p' "$0" | sed 's/^# //'; exit 0 ;;
    -*) echo "unknown option: $arg" >&2; exit 2 ;;
    *) REF=$arg ;;
  esac
done

REPO_DIR=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d "${TMPDIR:-/tmp}/pollux-preflight.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
WORK=$(cd "$WORK" && pwd -P) # the physical path, as dotnet prints it (/var is /private/var on macOS)
EXPORT="$WORK/export"
FAILED=""

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 TESTINGPLATFORM_TELEMETRY_OPTOUT=1
# Messages in English, so the NU1403 filter and the report read the same everywhere.
export DOTNET_CLI_UI_LANGUAGE=en

step() { printf '\n== %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*"; FAILED="$FAILED
  - $*"; }

# --- 1. Export -------------------------------------------------------------------------------------------------------

# Copy of homelab-forgejo publish-github/default.publicignore (the paths publish-github never publishes).
# Keep in sync; PUBLICIGNORE_DEFAULT=<file> uses another copy.
default_publicignore() {
  if [ -n "${PUBLICIGNORE_DEFAULT:-}" ]; then cat "$PUBLICIGNORE_DEFAULT"; return; fi
  cat <<'EOF'
.forgejo/
.publicignore
.claude/
CLAUDE.md
AGENTS.md
.env
.env.*
!.env.example
EOF
}

step "Export $REF as the public copy"
COMMIT=$(git -C "$REPO_DIR" rev-parse --verify -q "$REF^{commit}") || { echo "not a commit: $REF" >&2; exit 2; }
mkdir "$EXPORT"
git -C "$REPO_DIR" archive "$COMMIT" | tar -x -C "$EXPORT" || { echo "git archive failed" >&2; exit 2; }
default_publicignore > "$WORK/ignore"
git -C "$REPO_DIR" show "$COMMIT:.publicignore" >> "$WORK/ignore" 2>/dev/null || true
# Same matching as publish-github (git ls-files --ignored on the tag's tree), via a scratch index for <ref>.
GIT_INDEX_FILE="$WORK/index" git -C "$REPO_DIR" read-tree "$COMMIT"
GIT_INDEX_FILE="$WORK/index" git -C "$REPO_DIR" ls-files --cached --ignored --exclude-from="$WORK/ignore" \
  > "$WORK/dropped"
while IFS= read -r path; do rm -f "$EXPORT/$path"; done < "$WORK/dropped"
find "$EXPORT" -type d -empty -delete 2>/dev/null || true
echo "commit $(git -C "$REPO_DIR" log -1 --format='%h %s' "$COMMIT"); dropped $(wc -l < "$WORK/dropped" | tr -d ' ') files"

# --- 2. Language -----------------------------------------------------------------------------------------------------

# Umlauts (as alternatives, not a bracket: byte-safe in any locale) and common German function words in prose. Fenced code blocks and `code spans` are removed first,
# so key names such as `Ctrl+ö` stay allowed.
step "Published Markdown is English"
lang_hits="$WORK/lang"
: > "$lang_hits"
(cd "$EXPORT" && { find . -maxdepth 1 -name '*.md' -type f; find docs -name '*.md' -type f 2>/dev/null; }) \
  | sed 's|^\./||' | sort > "$WORK/mdfiles"
while IFS= read -r md; do
  awk '/^[[:space:]]*(```|~~~)/ { fence = !fence; next } !fence { gsub(/`[^`]*`/, ""); print FNR": "$0 }' "$EXPORT/$md" \
    | grep -E '(ä|ö|ü|Ä|Ö|Ü|ß)|(^|[^[:alpha:]])(und|nicht|oder|wird|werden|sind|auch|ist|eine|einen|wenn|dass|für|über|mit)([^[:alpha:]]|$)' \
    | sed "s|^|$md:|" >> "$lang_hits"
done < "$WORK/mdfiles"
if [ -s "$lang_hits" ]; then
  cat "$lang_hits"
  fail "German text in published Markdown (above); the public repo is English. Put key names in \`code spans\`."
else
  echo "ok: $(wc -l < "$WORK/mdfiles" | tr -d ' ') files"
fi

# --- 3./4. Pollux.App with release.yml's publish flags ---------------------------------------------------------------

RELEASE_YML="$EXPORT/.github/workflows/release.yml"
step "Pollux.App with release.yml's publish flags"
publish_line=$(grep -E '^[[:space:]]*run:[[:space:]]*dotnet publish ' "$RELEASE_YML" 2>/dev/null | head -n1 \
  | sed -E 's/^[[:space:]]*run:[[:space:]]*dotnet publish //')
# Matrix entries as "platform rid" lines (each "- platform:" followed by its "rid:").
matrix=$(awk '/- platform:/ { p = $3 } /^[[:space:]]+rid:/ && p != "" { print p, $2; p = "" }' "$RELEASE_YML" 2>/dev/null)
if [ -z "$publish_line" ] || [ -z "$matrix" ]; then
  fail "release.yml: no 'run: dotnet publish' line or no platform/rid matrix found"
  matrix=""
fi

# Turns publish arguments into MSBuild arguments for restore and evaluation: -c/-r become properties, -o is dropped,
# _IsPublishing is what dotnet publish adds itself. Prints one argument per line.
msbuild_args() {
  set -f
  # shellcheck disable=SC2086 # word splitting of the workflow's command line is intended
  set -- $1
  set +f
  while [ $# -gt 0 ]; do
    case $1 in
      -c|--configuration) echo "-p:Configuration=$2"; shift ;;
      -r|--runtime) echo "-p:RuntimeIdentifier=$2"; shift ;;
      -o|--output) shift ;;
      *) echo "$1" ;;
    esac
    shift
  done
  echo "-p:_IsPublishing=true"
}

# True when the restore log's only errors are NU1403 for packages a source-built SDK ships itself (ILLink, ILCompiler).
# Anything else (NU1004: lock file out of date, other packages' hashes) is a real lock file problem.
only_sdk_hash_errors() {
  sed -nE 's/.*error ([A-Z]+[0-9]+): ?(.*)$/\1 \2/p' "$1" | sed -E 's/ *\[[^]]*\]$//; s/ +$//' | sort -u > "$1.errors"
  [ -s "$1.errors" ] || return 1
  ! grep -vE '^NU1403( Package content hash validation failed for (Microsoft\.NET\.ILLink\.Tasks|Microsoft\.DotNet\.ILCompiler)\.[0-9].*)?$' \
    "$1.errors" >/dev/null
}

if [ -n "$matrix" ]; then
  echo "dotnet publish $publish_line"
  while read -r platform rid; do
    # shellcheck disable=SC2016 # the workflow's literal ${{ }} expressions
    line=${publish_line//'${{ matrix.platform }}'/$platform}
    # shellcheck disable=SC2016
    line=${line//'${{ matrix.rid }}'/$rid}
    msbuild_args "$line" > "$WORK/args"
    # Bash 3.2 has no mapfile; the workflow's publish arguments contain no spaces.
    args=$(tr '\n' ' ' < "$WORK/args")

    # stdin is the matrix here-doc: dotnet must not read it.
    # shellcheck disable=SC2086
    actual=$(cd "$EXPORT" && dotnet msbuild $args -getProperty:RuntimeIdentifier 2>&1 < /dev/null)
    if [ "$actual" = "$rid" ]; then
      echo "ok: $platform targets $rid"
    else
      echo "$actual" | sed "s|$EXPORT/||g" | tail -n 20
      fail "$platform: Pollux.App evaluates to RuntimeIdentifier '$actual', release.yml expects '$rid' (NETSDK1032 on GitHub)"
    fi

    # shellcheck disable=SC2086
    if (cd "$EXPORT" && CI=true dotnet restore $args -p:EnableWindowsTargeting=true) > "$WORK/restore.log" 2>&1 < /dev/null
    then
      echo "ok: $platform locked restore"
    elif [ "${CI:-}" != true ] && only_sdk_hash_errors "$WORK/restore.log"; then
      echo "warning: $platform locked restore: only the SDK-shipped ILLink/ILCompiler hashes differ (source-built SDK?)."
      echo "         Dependency graph matches the lock files; the full check runs on CI (CI=true)."
    else
      grep -E 'error|warning NU' "$WORK/restore.log" | sed -E "s|$EXPORT/||g; s/ +\[[^]]*\]$//" | sort -u | tail -n 20
      fail "$platform: locked restore of Pollux.App with release.yml's flags failed (lock files or -r/-p flags)"
    fi
  done <<EOF
$matrix
EOF
fi

# --- 5. Tests on the export ------------------------------------------------------------------------------------------

if [ "$RUN_TESTS" = true ]; then
  for project in tests/Pollux.Core.Tests tests/Pollux.Copilot.Tests; do
    step "dotnet test $project (public copy)"
    if (cd "$EXPORT" && dotnet test --project "$project" -c Release) > "$WORK/test.log" 2>&1; then
      tail -n 3 "$WORK/test.log"
    else
      grep -E 'failed|error' "$WORK/test.log" | sed "s|$EXPORT/||g" | tail -n 40
      fail "$project fails on the public copy (does a test read a dropped path such as .forgejo/?)"
    fi
  done
fi

if [ -n "$FAILED" ]; then
  printf '\nRelease preflight FAILED for %s:%s\n' "$(git -C "$REPO_DIR" rev-parse --short "$COMMIT")" "$FAILED"
  exit 1
fi
printf '\nRelease preflight passed for %s.\n' "$(git -C "$REPO_DIR" rev-parse --short "$COMMIT")"
