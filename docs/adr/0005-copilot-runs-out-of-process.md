# Copilot runs out of process, opt-in

Pollux integrates GitHub Copilot through the GitHub Copilot SDK, which drives the Copilot CLI as a separate child process over stdio. The Pollux process itself still opens no network connections; all Copilot traffic happens inside the CLI process. The integration is off by default and only starts the CLI after the user explicitly enables it.

This narrows ADR 0001 rather than abandoning it: Copilot is treated like `winget`, an external tool the user opted into. We rejected allowing Pollux itself to talk to the network (weakens the guarantee for every future feature) and handoff-only integration (too limited for using Copilot across Pollux).

## Consequences

- ADR 0001's "No built-in AI" no longer holds; its core rule (the Pollux process makes zero network calls) still does.
- With Copilot disabled, Pollux behaves exactly as before: no CLI process, no network.
- Corporate controls (Copilot seat, org CLI policy, proxy) apply to the CLI, not to Pollux.
- The SDK and CLI are new dependencies and must stay compatible with Native AOT (ADR 0002).
- Pollux bundles a pinned Copilot CLI (~65–70 MB compressed per architecture) instead of using a user-installed one: the SDK only accepts a CLI speaking its exact protocol version, so an independently updated CLI (e.g. via winget) could silently break Copilot until Pollux is updated. A configurable CLI path remains as an override.
- With Copilot enabled the CLI sends its own product telemetry to GitHub (process-level events, crash reports, experiment assignments; the endpoint is chosen by GitHub per account) that Pollux cannot switch off. Pollux disables session telemetry and never enables OpenTelemetry (it removes `OTEL_*` and `COPILOT_OTEL_*` from the CLI's environment); what is collected is governed by the user's Copilot plan / org policy.
