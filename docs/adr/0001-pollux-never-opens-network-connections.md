# Pollux never opens network connections

Pollux itself makes zero network calls: no telemetry, no crash reporting, no self-update check, no online icons or AI services. Data security on a corporate machine outranks convenience. The only network activity happens inside external tools the user explicitly opted into (e.g. `winget`), launched as separate processes.

## Consequences

- Pollux updates are installed manually.
- No built-in AI in the Pollux process; GitHub Copilot runs as an opt-in external process (ADR 0005).
- Any dependency that phones home is disqualified.
