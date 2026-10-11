# git only runs in Trusted Folders

A repository's own `.git/config` and `.gitattributes` can make `git status` run arbitrary commands (clean/process filters), so browsing an extracted zip or a shared repo could execute code. Pollux therefore starts git only inside Trusted Folders the user marked explicitly; elsewhere it reads the branch from `HEAD` itself without starting a process. Inside Trusted Folders it additionally refuses repos whose local config defines filters, diff/merge drivers, includes or command settings.

## Considered Options

- Scanning `.git/config` for dangerous keys everywhere: a denylist against git's growing config surface; kept only as a second line of defence.
- Reading the git index without git.exe: no process, but reimplementing status (ignore rules, racy timestamps, hashing) is large.
- Turning git status off by default: safe, but loses the feature where it is most useful.
