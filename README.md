# LlrpReaderStudio

Standalone WPF application for operating LLRP readers through `LLRPCSharp`.

The application is split into a UI-independent Core project and a WPF shell:

- `LlrpReaderStudio.Core` contains application services and state.
- `LlrpReaderStudio.Wpf` contains the desktop UI.

During the migration, the Core project references the sibling `LLRPCSharp`
source tree. It will switch to the published `LlrpSdk` packages after the SDK
dependency boundary is validated.

## Planning

The implementation plan for the Impinj Reader focused application is available
in [docs/impinj-reader-studio-plan.md](docs/impinj-reader-studio-plan.md).
