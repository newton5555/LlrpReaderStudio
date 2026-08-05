using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderStudio.Core;
using LlrpSdk;

namespace LlrpReaderStudio.ViewModels;

public partial class TagMemoryViewModel : PageViewModelBase
{
    private readonly ReaderFleetService fleet;
    private IReadOnlyList<ReaderOperationTarget> operationReaders = [];

    [ObservableProperty]
    private string targetMatch = string.Empty;

    [ObservableProperty]
    private string targetType = "EPC";

    [ObservableProperty]
    private MemoryBankItem selectedMemoryBank = new(TagMemoryBank.User, "User");

    [ObservableProperty]
    private string wordPointer = "0";

    [ObservableProperty]
    private string wordCount = "6";

    [ObservableProperty]
    private string accessPassword = "00000000";

    [ObservableProperty]
    private string tagData = string.Empty;

    [ObservableProperty]
    private string statusMessage = "Target one exact EPC for Gen2 tag memory access.";

    [ObservableProperty]
    private string operationReaderName = string.Empty;

    [ObservableProperty]
    private string operationResultText = string.Empty;

    public TagMemoryViewModel(ReaderFleetService fleet)
    {
        this.fleet = fleet;
        PageTitle = "Tag Memory";
    }

    public IReadOnlyList<MemoryBankItem> MemoryBankOptions { get; } =
    [
        new(TagMemoryBank.ElectronicProductCode, "EPC"),
        new(TagMemoryBank.Tid, "TID"),
        new(TagMemoryBank.User, "User"),
        new(TagMemoryBank.Reserved, "Reserved"),
    ];

    public sealed record MemoryBankItem(TagMemoryBank Value, string Display);

    public void SetOperationReaders(IEnumerable<ReaderItemViewModel> readers)
    {
        operationReaders = readers
            .Where(static reader => reader.IsEnabled)
            .Select(static reader => new ReaderOperationTarget(reader.Id, reader.Name))
            .ToArray();

        // Tag memory access targets a single device: the first enabled reader.
        OperationReaderName = operationReaders.Count > 0 ? operationReaders[0].Name : string.Empty;
    }

    [RelayCommand]
    private async Task ReadMemoryAsync()
    {
        ReaderOperationTarget reader = GetSingleOperationReader();
        if (reader.Id == Guid.Empty)
        {
            return;
        }

        try
        {
            // Tag access requires an established LLRP connection; ConnectAsync is idempotent
            // (no-op when already connected), mirroring the inventory start path.
            await fleet.ConnectAsync(reader.Id, CancellationToken.None);

            TagSelection selection = BuildSelection();
            var req = new ReadTagRequest
            {
                Selection = selection,
                MemoryBank = SelectedMemoryBank.Value,
                WordPointer = ushort.Parse(WordPointer, CultureInfo.InvariantCulture),
                WordCount = ushort.Parse(WordCount, CultureInfo.InvariantCulture),
                AccessPassword = ParseUInt32Hex(AccessPassword),
            };

            TagAccessResult result = await fleet.ReadTagMemoryAsync(reader.Id, req, CancellationToken.None);
            if (result.Operation.Success)
            {
                IReadOnlyList<ushort> words = result.Operation.ReadData ?? [];
                byte[] bytes = words.SelectMany(static w => new byte[] { (byte)(w >> 8), (byte)(w & 0xFF) }).ToArray();
                TagData = HexCodec.FormatBytes(bytes);
                OperationResultText = $"SUCCESS: read {words.Count * 2} bytes from {SelectedMemoryBank.Display}.";
                StatusMessage = $"{reader.Name}: Read {words.Count * 2} bytes from {SelectedMemoryBank.Display}.";
            }
            else
            {
                OperationResultText = $"FAILED: {result.Operation.Error ?? "no data"}.";
                StatusMessage = $"{reader.Name}: Read failed: {result.Operation.Error ?? "no data"}.";
            }
        }
        catch (Exception ex)
        {
            OperationResultText = $"FAILED: {ex.Message}";
            StatusMessage = $"Read failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task WriteMemoryAsync()
    {
        ReaderOperationTarget reader = GetSingleOperationReader();
        if (reader.Id == Guid.Empty)
        {
            return;
        }

        try
        {
            // Tag access requires an established LLRP connection; ConnectAsync is idempotent.
            await fleet.ConnectAsync(reader.Id, CancellationToken.None);

            TagSelection selection = BuildSelection();
            byte[] data = HexCodec.ParseBytes(TagData);

            if (data.Length == 0)
            {
                StatusMessage = "Enter payload bytes (hex words) before writing.";
                return;
            }

            var writeWords = new ushort[data.Length / 2];
            for (int i = 0; i < writeWords.Length; i++)
            {
                writeWords[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
            }

            var req = new WriteTagRequest
            {
                Selection = selection,
                MemoryBank = SelectedMemoryBank.Value,
                WordPointer = ushort.Parse(WordPointer, CultureInfo.InvariantCulture),
                WriteData = writeWords,
                AccessPassword = ParseUInt32Hex(AccessPassword),
            };

            TagAccessResult result = await fleet.WriteTagMemoryAsync(reader.Id, req, CancellationToken.None);
            if (result.Operation.Success)
            {
                OperationResultText = $"SUCCESS: wrote {writeWords.Length * 2} bytes to {SelectedMemoryBank.Display}.";
                StatusMessage = $"{reader.Name}: Wrote {writeWords.Length * 2} bytes to {SelectedMemoryBank.Display}.";
            }
            else
            {
                OperationResultText = $"FAILED: {result.Operation.Error ?? "no data"}.";
                StatusMessage = $"{reader.Name}: Write failed: {result.Operation.Error ?? "no data"}.";
            }
        }
        catch (Exception ex)
        {
            OperationResultText = $"FAILED: {ex.Message}";
            StatusMessage = $"Write failed: {ex.Message}";
        }
    }

    private TagSelection BuildSelection()
    {
        byte[] data = HexCodec.ParseBytes(TargetMatch);
        if (data.Length == 0)
        {
            throw new InvalidOperationException("Enter a Target match (hex) before memory access.");
        }

        // EPC targets match the EPC value starting after the 32-bit EPC header; TID targets match
        // the TID bank from bit 0. Both use a full-width mask so the match is exact.
        bool isTid = TargetType.Equals("TID", StringComparison.OrdinalIgnoreCase);
        return new TagSelection
        {
            MemoryBank = isTid ? TagMemoryBank.Tid : TagMemoryBank.ElectronicProductCode,
            BitPointer = isTid ? (ushort)0 : (ushort)32,
            BitLength = checked((ushort)(data.Length * 8)),
            Mask = Enumerable.Repeat((byte)0xFF, data.Length).ToArray(),
            Data = data,
        };
    }

    private static uint ParseUInt32Hex(string value) =>
        uint.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    private ReaderOperationTarget GetSingleOperationReader()
    {
        if (operationReaders.Count == 0)
        {
            StatusMessage = "Turn ON one data source before tag memory access.";
            return new ReaderOperationTarget(Guid.Empty, string.Empty);
        }

        // Tag memory access targets a single device: the first enabled reader (same preparation
        // as the inventory start path — the operation itself connects when needed).
        return operationReaders[0];
    }

    private readonly record struct ReaderOperationTarget(Guid Id, string Name);
}
