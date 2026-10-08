# Unpackaged, Native AOT, per-user install

Pollux ships as an unpackaged, self-contained WinUI 3 / .NET app compiled with Native AOT and trimming, installed per user under `%LocalAppData%\Pollux`. This runs without admin rights or MSIX signing on a locked-down corporate machine, and AOT is the biggest single lever for startup time. Builds target x64 (work laptop) and ARM64 (dev VM on Apple Silicon).

## Considered Options

- **MSIX**: cleaner install/update story, but needs signing and often IT approval.
- **JIT/framework-dependent**: smaller binary, noticeably slower cold start, and reflection-heavy code that later blocks AOT.

## Consequences

- Code and dependencies must be AOT/trim-safe (no reflection-based serialization; source generators instead).
