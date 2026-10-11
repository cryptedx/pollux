# Guard Rules fail closed on shell they cannot analyse

The Guard Rules judge shell commands by parsing them; PowerShell expressions (parentheses, string concatenation, `[Type]::` calls, `New-Object`) build paths and targets the parser cannot see, which let Copilot read protected secrets in Allow All. Any statement containing such constructs now always asks, a pipeline into a writing cmdlet counts as a write to its upstream paths, and DNS/ping and persistent system changes (`setx`, `git config --global`) join the blocked network and persistence commands. Plain unknown commands whose arguments analyse cleanly still run without asking.

## Considered Options

- An allowlist of known commands in Allow All: closes unknown-command gaps too, but makes Allow All ask almost as often as Assisted.
