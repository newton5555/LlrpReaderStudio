namespace LlrpReaderStudio.Core;

/// <summary>LLRP protocol version selection for a reader connection.</summary>
public enum LlrpProtocolVersionOption
{
    /// <summary>Probe for 1.1 and retain 1.0.1 when the reader rejects the probe (recommended).</summary>
    Auto = 0,

    /// <summary>Force LLRP 1.0.1.</summary>
    Force101 = 1,

    /// <summary>Require LLRP 1.1; fail the connection if the reader cannot negotiate it.</summary>
    Force11 = 2,
}

public sealed record ReaderProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Reader";
    public required string Host { get; init; }
    public int Port { get; init; } = 5084;
    public bool EnableImpinjExtensions { get; init; } = true;
    public LlrpProtocolVersionOption LlrpVersion { get; init; } = LlrpProtocolVersionOption.Auto;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "The LLRP port must be from 1 through 65535.");
        }
    }
}

public enum StudioReaderState
{
    Disconnected,
    Connecting,
    Connected,
    Stopping,
    Disconnecting,
    Inventorying,
    Faulted,
}

public sealed record ReaderStatus(
    ReaderProfile Profile,
    StudioReaderState State,
    string? Model,
    string? Firmware,
    string? Error);

/// <summary>Result of a temporary connectivity probe for a not-yet-registered data source.</summary>
public sealed record ReaderProbeResult(string? Model, string? Firmware);
