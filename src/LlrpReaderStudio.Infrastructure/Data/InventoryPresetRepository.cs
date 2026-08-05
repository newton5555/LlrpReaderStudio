using LlrpReaderStudio.Infrastructure.Data.Entities;
using LlrpSdk;
using LlrpSdk.Extensions.Impinj;
using Microsoft.EntityFrameworkCore;

namespace LlrpReaderStudio.Infrastructure.Data;

public sealed class InventoryPresetRepository
{
    private readonly IDbContextFactory<StudioDbContext> dbContextFactory;

    public InventoryPresetRepository(IDbContextFactory<StudioDbContext> dbContextFactory)
    {
        this.dbContextFactory = dbContextFactory;
    }

    public async Task<ReaderSettings?> LoadDefaultAsync(Guid readerId, CancellationToken cancellationToken = default)
    {
        await using StudioDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        string name = GetDefaultPresetName(readerId);
        InventoryPresetEntity? entity = await dbContext.InventoryPresets
            .AsNoTracking()
            .FirstOrDefaultAsync(preset => preset.Name == name, cancellationToken)
            .ConfigureAwait(false);

        return entity is null
            ? null
            : DeserializeReaderSettings(entity.SettingsJson);
    }

    public async Task SaveDefaultAsync(Guid readerId, ReaderSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using StudioDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        string name = GetDefaultPresetName(readerId);
        InventoryPresetEntity? entity = await dbContext.InventoryPresets
            .FirstOrDefaultAsync(preset => preset.Name == name, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new InventoryPresetEntity
            {
                Id = Guid.NewGuid(),
                Name = name,
                SchemaVersion = 1,
            };
            dbContext.InventoryPresets.Add(entity);
        }

        entity.SettingsJson = ReaderSettingsSerializer.SerializeToJson(
            settings,
            [ImpinjReaderExtension.Instance]);
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetDefaultPresetName(Guid readerId) => $"reader:{readerId}:default";

    private static ReaderSettings DeserializeReaderSettings(string json)
    {
        try
        {
            return ReaderSettingsSerializer.DeserializeFromJson(json, [ImpinjReaderExtension.Instance]);
        }
        catch
        {
            return new ReaderSettings { Inventory = InventorySettingsSerializer.DeserializeFromJson(json) };
        }
    }
}
