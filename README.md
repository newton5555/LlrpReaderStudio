# LlrpReaderStudio

Standalone WPF application for operating LLRP RFID readers, built on the
[`LlrpSdk`](https://www.nuget.org/packages/LlrpSdk) / 
[`LlrpSdk.Extensions.Impinj`](https://www.nuget.org/packages/LlrpSdk.Extensions.Impinj)
NuGet packages (**0.8.0**, no SDK source reference).

## Features

- **Reader management**: add / remove / enable data sources, Zeroconf (mDNS) discovery, SQLite persistence.
- **Inventory**: aggregated tag listing (EPC, TID via FastID, RF phase / Doppler, counts, read rate),
  unique tag count, per-reader start/stop, Clear (resets UI and aggregation state).
- **Device settings** (per reader, persisted to SQLite presets), organized as tab pages:
  - *Inventory tab* — LLRP inventory settings:
    - Antenna power: all antennas (single `AntennaId=0` config) or per-antenna editor (expand to edit
      each antenna individually); Tx Power / Rx Sensitivity picked from the reader capability tables.
    - RF mode (mode id with link parameters from the capability table), session, population estimate,
      report interval.
    - Gen2 filters: Filter 1 / Filter 2 with enable switches, mask / bit length / offset / memory bank,
      and Match / Non-Match actions; state-aware filters (Target: S0-S3 / Selected Flag + 8 actions)
      gated by reader `CanDoTagInventoryStateAwareSingulation`.
    - Search mode (single / dual target / tag focus, via inventory-command extension).
    - Frequency: Disabled / Auto Select / Channel List with a selectable channel list
      (check boxes over the capability hop table, scrollable; channel selections survive setting refreshes).
    - Low duty cycle (empty-field timeout, field ping interval).
    - GPI start/stop triggers + GPI debounce.
  - *Diagnostics tab* — GPO control (GPO 1-4 immediate switches).
- **Tag memory**: read / write EPC memory banks.
- **Logging**: two independent Serilog pipelines — app log `studio-yyyyMMdd.log` and SDK log
  `sdk-yyyyMMdd.log` (async sinks, 50 MB size rolling + daily rolling, 14 files retained;
  Information level for Release, Debug for Debug builds), plus Debug output in Visual Studio.

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

- `LlrpSdk` / `LlrpSdk.Extensions.Impinj` (**0.8.0**, NuGet)
- CommunityToolkit.Mvvm, MahApps.Metro, FontAwesome.Sharp
- Microsoft.EntityFrameworkCore.Sqlite, Zeroconf
- Serilog (+ Serilog.Sinks.Async / Serilog.Sinks.File), Serilog.Extensions.Logging,
  Microsoft.Extensions.Logging.Debug

## Planning

The implementation plan for the Impinj Reader focused application is available
in [docs/impinj-reader-studio-plan.md](docs/impinj-reader-studio-plan.md).
