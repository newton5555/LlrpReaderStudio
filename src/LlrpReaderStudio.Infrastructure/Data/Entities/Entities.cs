using System.ComponentModel.DataAnnotations;

namespace LlrpReaderStudio.Infrastructure.Data.Entities;

public sealed class ReaderProfileEntity
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(250)]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 5084;

    public bool EnableImpinjExtensions { get; set; } = true;

    public bool AutoReconnect { get; set; } = false;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ReaderPresetEntity
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int SchemaVersion { get; set; } = 1;

    [Required]
    public string SettingsJson { get; set; } = "{}";

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class InventoryPresetEntity
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int SchemaVersion { get; set; } = 1;

    [Required]
    public string SettingsJson { get; set; } = "{}";

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TagListEntity
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    [MaxLength(20)]
    public string ColorHex { get; set; } = "#5EEAD4";

    public List<TagListEntryEntity> Entries { get; set; } = [];
}

public sealed class TagListEntryEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid TagListId { get; set; }

    public TagListEntity? TagList { get; set; }

    [Required]
    [MaxLength(128)]
    public string EpcHex { get; set; } = string.Empty;

    [MaxLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? ColorHex { get; set; }
}

public sealed class InventoryRunEntity
{
    [Key]
    public Guid Id { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    [MaxLength(50)]
    public string StopReason { get; set; } = "Manual";

    public long TotalReadCount { get; set; }

    public int UniqueTagCount { get; set; }

    public string? LogFilePath { get; set; }
}

public sealed class AppSettingEntity
{
    [Key]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    [Required]
    public string Value { get; set; } = string.Empty;
}
