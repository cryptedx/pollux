# Pollux

A fast, offline, keyboard-friendly file manager for Windows 11 (WinUI 3, .NET, Native AOT).

Start with [GLOSSARY.md](GLOSSARY.md), [docs/adr/](docs/adr/) and the [v1 spec](.scratch/v1-core/spec.md).

## Build

Requires Windows 11, the .NET 10 SDK and Visual Studio 2026 with the *WinUI application development* and *Desktop development with C++* workloads (the C++ toolchain is what Native AOT links with).

```powershell
dotnet test --project tests/Pollux.Core.Tests
dotnet publish src/Pollux.App -c Release -p:Platform=x64 -r win-x64 -o artifacts/win-x64
```

`Pollux.Core` has no Windows dependency, so its tests also run on macOS/Linux.
