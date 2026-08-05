# LlrpReaderStudio

Standalone WPF application for operating LLRP RFID readers, built on the
[`LlrpSdk`](https://www.nuget.org/packages/LlrpSdk) packages (NuGet, no SDK source dependency).

## Features

- **Reader management**: add / remove / enable data sources, Zeroconf (mDNS) discovery, SQLite persistence.
- **Inventory**: aggregated tag listing (EPC, TID via FastID, counts, read rate), unique tag count,
  per-reader start/stop, Clear (resets UI and aggregation state).
- **Device settings** (per reader, persisted to SQLite presets):
  - LLRP inventory settings: antennas (all / individual), RF mode, session, population, report interval,
    Gen2 filters, GPI start/stop triggers, tag memory access.
  - Impinj extensions (via `LlrpSdk.Extensions.Impinj`): FastID / RF phase / Doppler reports,
    search mode (inventory command), fixed frequency / channel list, low duty cycle, GPI debounce.
- **Tag memory**: read / write EPC memory banks.
- **Logging**: Debug (Visual Studio output) + rolling file under `%AppData%\LlrpReaderStudio\logs\`.

## Projects

| Project | Purpose |
|---|---|
| `LlrpReaderStudio.Core` | Reader fleet service, sessions, tag aggregation (UI-independent) |
| `LlrpReaderStudio.Infrastructure` | SQLite repositories (data sources, presets), Zeroconf discovery |
| `LlrpReaderStudio.Wpf` | Desktop UI (MVVM) |
| `LlrpReaderStudio.Core.Tests` | Core unit tests |

## Requirements

- .NET 10 SDK
- Windows (WPF)

## Build & test

```powershell
dotnet build LlrpReaderStudio.slnx
dotnet test  LlrpReaderStudio.slnx --no-build
```

## Packages

- `LlrpSdk` / `LlrpSdk.Extensions.Impinj` (0.7.x)
- CommunityToolkit.Mvvm, MahApps.Metro, FontAwesome.Sharp
- Microsoft.EntityFrameworkCore.Sqlite, Zeroconf
- Serilog (file sink) + Microsoft.Extensions.Logging.Debug

## Planning

The implementation plan for the Impinj Reader focused application is available
in [docs/impinj-reader-studio-plan.md](docs/impinj-reader-studio-plan.md).
