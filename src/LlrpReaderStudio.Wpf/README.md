# LLRP Reader Studio

This is the first WPF application built on the high-level `LlrpSdk` API. It is an
application-layer reference, not another protocol implementation.

Implemented in this baseline:

- manual LLRP reader profiles and multiple simultaneous reader sessions;
- LLRP transport only, using port `5084` by default;
- mDNS discovery for `_llrp._tcp.local.` readers;
- aggregated inventory observations across connected readers;
- exact-EPC Gen2 memory read and write;
- device settings query, SDK default settings, draft apply, and explicit inventory start;
- tag logging and settings workspace.

This WPF app deliberately excludes IoT-device management, RShell, RDD/FDD capture,
application-side Tags of Interest, and spatial reader capabilities.

Run on Windows:

```powershell
dotnet run --project src/LlrpReaderStudio.Wpf/LlrpReaderStudio.Wpf.csproj
```
