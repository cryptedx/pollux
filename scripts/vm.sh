#!/usr/bin/env bash
# Builds, runs and inspects Pollux in the Windows ARM64 VM from the Mac, over ssh.
# Run "scripts/vm.sh help" for the commands. Needs: ssh access to the VM (host alias, default windows-vm, with
# PowerShell as its shell), git, and node 22+ on the Mac for panel-eval/panel-shot.
#
# Every PowerShell step runs as a .ps1 from scripts/vm/ (copied to the VM on each call), never as an inline
# ssh one-liner: nested zsh/PowerShell quoting is where those break. Builds go into throwaway clones under
# POLLUX_VM_ROOT; nothing else on the VM (other checkouts, an installed Pollux, other apps) is touched.
# Remote commands are built on the Mac on purpose (SC2029); '\\' in tr is a backslash, not a quote (SC1003).
# shellcheck disable=SC2029,SC1003
set -euo pipefail
TMPDIR=${TMPDIR:-/tmp}

VM_HOST=${POLLUX_VM_HOST:-windows-vm}
VM_ROOT=${POLLUX_VM_ROOT:-'C:\dev\pollux-vm'}
VM_CDP_PORT=${POLLUX_VM_CDP_PORT:-9222}
LOCAL_CDP_PORT=${POLLUX_VM_LOCAL_CDP_PORT:-19222}
REPO_DIR=$(cd "$(dirname "$0")/.." && pwd)
TOOLS="$VM_ROOT\\.tools"

usage() {
  cat <<'EOF'
usage: scripts/vm.sh <command> [args]

  install [<ref>]          bundle <ref> (branch, tag, origin/<branch>; default HEAD, committed state only),
                           copy it to the VM, check it out in the clone <root>\<slug> and dotnet publish
                           ARM64 Release there. The clone becomes the current one.
  release <tag>            download the official GitHub release zip (SHA256-checked) into <root>\release-<tag>
                           and make it the current one.
  run [--command <id>]...  (re)start the current clone's Pollux in the VM desktop (stopping any other clone's),
      [--shared-profile]   with WebView2 remote debugging; command ids are passed as --command. Own profile
                           under <clone>\profile, seeded from the real one, unless --shared-profile.
  command <id>...          hand command ids to the running clone's Pollux (e.g. copilot.togglePanel).
  shot [--window]          screenshot the VM desktop (or only the clone's Pollux window) to a PNG on the Mac;
                           prints its path.
  panel-eval <js>...       evaluate JavaScript in the Copilot panel page over CDP; prints one JSON line each.
  panel-shot               PNG of the Copilot panel page itself; prints its path.
  cdp <args>...            scripts/cdp.mjs through the tunnel (e.g. cdp --page notes list).
  test [<project>]...      dotnet test in the current clone (default: both test projects).
  stop                     stop the current clone's processes.
  status                   list clones, the current one (*), and their running processes.
  clean [--all | <slug>]   stop and delete the current clone (or all, or one by slug).
  setup                    install node in the VM (winget, user scope) so the Copilot tests can run there.

Options for every command: --slug <slug> picks a clone other than the current one.
Environment: POLLUX_VM_HOST (windows-vm), POLLUX_VM_ROOT (C:\dev\pollux-vm), POLLUX_VM_CDP_PORT (9222),
POLLUX_VM_LOCAL_CDP_PORT (19222).
EOF
}

die() { echo "vm.sh: $*" >&2; exit 1; }

# Arguments travel through the remote PowerShell command line unquoted, so they are restricted to safe tokens.
SAFE_TOKEN='^[A-Za-z0-9._:,/\\+=-]+$'
safe() {
  [[ "$1" =~ $SAFE_TOKEN ]] || die "unsupported characters in '$1'"
  printf '%s' "$1"
}

# Runs a script from scripts/vm on the VM: remote_ps <script> [args...]
remote_ps() {
  local script=$1; shift
  ssh "$VM_HOST" "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File '$TOOLS\\$script' $*; exit \$LASTEXITCODE"
}

sync_tools() {
  ssh "$VM_HOST" "New-Item -ItemType Directory -Force -Path '$TOOLS' | Out-Null" \
    || die "cannot reach $VM_HOST over ssh"
  scp -q "$REPO_DIR"/scripts/vm/*.ps1 "$VM_HOST:$(printf '%s' "$TOOLS" | tr '\\' '/')/"
}

# Prints the clone directory of --slug, or of the current clone.
clone_dir() {
  if [[ -n "$SLUG" ]]; then
    printf '%s\\%s' "$VM_ROOT" "$SLUG"
  else
    local current
    current=$(ssh "$VM_HOST" "Get-Content -Raw '$VM_ROOT\\current.txt' -ErrorAction SilentlyContinue" | tr -d '\r\n ')
    [[ -n "$current" ]] || die "no current clone. Run: scripts/vm.sh install <ref>"
    printf '%s\\%s' "$VM_ROOT" "$current"
  fi
}

slug_of() {
  # Short and path-safe: the build nests deep under the clone and Windows paths are limited to 260 characters.
  printf '%s' "$1" | sed -e 's#^refs/heads/##' -e 's#^refs/tags/##' -e 's#^refs/remotes/##' -e 's#[^A-Za-z0-9._-]#-#g' | cut -c1-24
}

TUNNEL_SOCK=
open_tunnel() {
  TUNNEL_SOCK=$(mktemp -u "${TMPDIR%/}/pollux-vm-cdp.XXXXXX")
  ssh -M -S "$TUNNEL_SOCK" -fN -o ExitOnForwardFailure=yes \
    -L "$LOCAL_CDP_PORT:127.0.0.1:$VM_CDP_PORT" "$VM_HOST" \
    || die "tunnel failed; is local port $LOCAL_CDP_PORT already in use? (POLLUX_VM_LOCAL_CDP_PORT)"
  trap close_tunnel EXIT
}
close_tunnel() {
  [[ -n "$TUNNEL_SOCK" ]] && ssh -S "$TUNNEL_SOCK" -O exit "$VM_HOST" 2>/dev/null || true
}

cdp() {
  command -v node >/dev/null || die "node is needed on the Mac for CDP"
  open_tunnel
  node "$REPO_DIR/scripts/cdp.mjs" --port "$LOCAL_CDP_PORT" "$@"
}

[[ $# -gt 0 ]] || { usage; exit 1; }
CMD=$1; shift
SLUG=
ARGS=()
while [[ $# -gt 0 ]]; do
  case $1 in
    --slug) SLUG=$(safe "${2:?--slug needs a value}"); shift 2 ;;
    *) ARGS+=("$1"); shift ;;
  esac
done
set -- ${ARGS[@]+"${ARGS[@]}"}

case $CMD in
  help|-h|--help) usage ;;

  install)
    ref=${1:-HEAD}
    full=$(git -C "$REPO_DIR" rev-parse --symbolic-full-name "$ref" 2>/dev/null || true)
    if [[ "$ref" == HEAD && -n "$full" && "$full" != HEAD ]]; then ref=$full; fi
    [[ -n "$full" ]] || die "'$ref' is not a branch, tag or HEAD (bundles carry refs, not bare commits)"
    sha=$(git -C "$REPO_DIR" rev-parse --short "$full^{commit}")
    [[ -z "$(git -C "$REPO_DIR" status --porcelain --untracked-files=no)" ]] \
      || echo "vm.sh: note: uncommitted changes are not included; building $full ($sha)" >&2
    slug=${SLUG:-$(slug_of "$full")}
    [[ "$slug" != .tools ]] || die "reserved slug"
    tmp=$(mktemp -d "${TMPDIR%/}/pollux-vm.XXXXXX")
    git -C "$REPO_DIR" bundle create -q "$tmp/pollux.bundle" "$full"
    sync_tools
    scp -q "$tmp/pollux.bundle" "$VM_HOST:$(printf '%s' "$VM_ROOT" | tr '\\' '/')/$slug.bundle"
    rm -rf "$tmp"
    remote_ps vm.ps1 -Action build -Root "'$VM_ROOT'" -Slug "$(safe "$slug")" \
      -Bundle "'$VM_ROOT\\$slug.bundle'" -Ref "$(safe "$full")"
    ;;

  release)
    tag=$(safe "${1:?usage: release <tag>}")
    sync_tools
    remote_ps vm.ps1 -Action release -Root "'$VM_ROOT'" -Slug "$(safe "${SLUG:-release-$tag}")" -Tag "$tag"
    ;;

  run)
    commands=; shared=
    while [[ $# -gt 0 ]]; do
      case $1 in
        --command) commands=${commands:+$commands,}$(safe "${2:?--command needs an id}"); shift 2 ;;
        --shared-profile) shared=-SharedProfile; shift ;;
        *) die "run: unknown argument $1" ;;
      esac
    done
    sync_tools
    dir=$(clone_dir)
    remote_ps session.ps1 start.ps1 -Dir "$(safe "$dir")" -Port "$VM_CDP_PORT" -Restart \
      ${commands:+-Commands "$commands"} $shared
    ;;

  command)
    [[ $# -gt 0 ]] || die "usage: command <id>..."
    ids=; for id in "$@"; do ids=${ids:+$ids,}$(safe "$id"); done
    sync_tools
    remote_ps session.ps1 start.ps1 -Dir "$(safe "$(clone_dir)")" -Commands "$ids"
    ;;

  shot)
    window=
    sync_tools
    [[ "${1:-}" != --window ]] || window="-Dir $(safe "$(clone_dir)")"
    name="shot-$(date +%Y%m%d-%H%M%S).png"
    vmfile="$VM_ROOT\\.tools\\$name"
    # shellcheck disable=SC2086 # $window is "-Dir <path>" or empty
    remote_ps session.ps1 shot.ps1 -Out "$(safe "$vmfile")" $window
    out=$(mktemp -d "${TMPDIR%/}/pollux-vm-shot.XXXXXX")
    scp -q "$VM_HOST:$(printf '%s' "$vmfile" | tr '\\' '/')" "$out/$name"
    ssh "$VM_HOST" "Remove-Item '$vmfile'"
    echo "$out/$name"
    ;;

  panel-eval)
    [[ $# -gt 0 ]] || die "usage: panel-eval <js>..."
    cdp eval "$@"
    ;;

  panel-shot)
    out=$(mktemp -d "${TMPDIR%/}/pollux-vm-panel.XXXXXX")
    cdp shot "$out/panel-$(date +%Y%m%d-%H%M%S).png"
    ;;

  cdp) cdp "$@" ;;

  test)
    projects=
    for p in "$@"; do projects=${projects:+$projects,}$(safe "$(printf '%s' "$p" | tr '/' '\\')"); done
    sync_tools
    remote_ps vm.ps1 -Action test -Root "'$VM_ROOT'" ${SLUG:+-Slug "$SLUG"} ${projects:+-Project "$projects"}
    ;;

  stop|status)
    sync_tools
    remote_ps vm.ps1 -Action "$CMD" -Root "'$VM_ROOT'" ${SLUG:+-Slug "$SLUG"}
    ;;

  clean)
    sync_tools
    case ${1:-} in
      --all) remote_ps vm.ps1 -Action clean -Root "'$VM_ROOT'" -All ;;
      '') remote_ps vm.ps1 -Action clean -Root "'$VM_ROOT'" ${SLUG:+-Slug "$SLUG"} ;;
      *) remote_ps vm.ps1 -Action clean -Root "'$VM_ROOT'" -Slug "$(safe "$1")" ;;
    esac
    ;;

  setup)
    ssh "$VM_HOST" "if (Get-Command node -ErrorAction SilentlyContinue) { 'node ' + (node --version) + ' already installed' } else { winget install --id OpenJS.NodeJS.LTS --scope user --silent --accept-source-agreements --accept-package-agreements --disable-interactivity }"
    ;;

  *) usage; exit 1 ;;
esac
