using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderStudio.Core;
using LlrpSdk;

namespace LlrpReaderStudio.ViewModels;

public partial class TagMemoryViewModel : PageViewModelBase
{
    private readonly ReaderFleetService fleet;

    [ObservableProperty]
    private string targetEpc = string.Empty;

    [ObservableProperty]
    private TagMemoryBank memoryBank = TagMemoryBank.User;

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

    public TagMemoryViewModel(ReaderFleetService fleet)
    {
        this.fleet = fleet;
        PageTitle = "Tag Memory";
    }

    public IReadOnlyList<TagMemoryBank> MemoryBanks { get; } = Enum.GetValues<TagMemoryBank>();

    public Guid? SelectedReaderId { get; set; }
    public string? SelectedReaderName { get; set; }

    [RelayCommand]
    private async Task ReadMemoryAsync()
    {
        if (SelectedReaderId is not Guid readerId)
        {
            StatusMessage = "Select a reader in the sidebar first.";
            return;
        }

        try
        {
            TagSelection selection = BuildSelection();
            var req = new ReadTagRequest
            {
                Selection = selection,
                MemoryBank = MemoryBank,
                WordPointer = ushort.Parse(WordPointer, CultureInfo.InvariantCulture),
                WordCount = ushort.Parse(WordCount, CultureInfo.InvariantCulture),
                AccessPassword = ParseUInt32Hex(AccessPassword),
            };

            TagAccessResult result = await fleet.ReadTagMemoryAsync(readerId, req, CancellationToken.None);
            IReadOnlyList<ushort> words = result.Operation.ReadData ?? [];
            byte[] bytes = words.SelectMany(static w => new byte[] { (byte)(w >> 8), (byte)(w & 0xFF) }).ToArray();
            TagData = HexCodec.FormatBytes(bytes);
            StatusMessage = $"{SelectedReaderName}: Read {words.Count * 2} bytes from {MemoryBank}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Read failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task WriteMemoryAsync()
    {
        if (SelectedReaderId is not Guid readerId)
        {
            StatusMessage = "Select a reader in the sidebar first.";
            return;
        }

        try
        {
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
                MemoryBank = MemoryBank,
                WordPointer = ushort.Parse(WordPointer, CultureInfo.InvariantCulture),
                WriteData = writeWords,
                AccessPassword = ParseUInt32Hex(AccessPassword),
            };

            TagAccessResult result = await fleet.WriteTagMemoryAsync(readerId, req, CancellationToken.None);
            StatusMessage = $"{SelectedReaderName}: Wrote {writeWords.Length * 2} bytes to {MemoryBank}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Write failed: {ex.Message}";
        }
    }

    private TagSelection BuildSelection()
    {
        byte[] data = HexCodec.ParseBytes(TargetEpc);
        if (data.Length == 0)
        {
            throw new InvalidOperationException("Enter an exact Target EPC before memory access.");
        }

        return new TagSelection
        {
            MemoryBank = TagMemoryBank.ElectronicProductCode,
            BitPointer = 32,
            BitLength = checked((ushort)(data.Length * 8)),
            Mask = Enumerable.Repeat((byte)0xFF, data.Length).ToArray(),
            Data = data,
        };
    }

    private static uint ParseUInt32Hex(string value) =>
        uint.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
}
