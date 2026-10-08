# Pollux never opens network connections

Pollux itself makes zero network calls: no telemetry, no crash reporting, no self-update check, no online icons or AI services. Data security on a corporate machine outranks convenience. The only network activity happens inside external tools the user explicitly opted into (e.g. `winget`), launched as separate processes.

## Consequences

- Pollux updates are installed manually.
- No built-in AI; integration with GitHub Copilot / VS Code is limited to handing off paths and content locally.
- Any dependency that phones home is disqualified.
