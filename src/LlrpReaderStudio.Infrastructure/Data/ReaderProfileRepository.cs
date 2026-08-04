using LlrpReaderStudio.Core;
using LlrpReaderStudio.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace LlrpReaderStudio.Infrastructure.Data;

public sealed record SavedReaderProfile(ReaderProfile Profile, bool IsEnabled);

public sealed class ReaderProfileRepository
{
    private readonly IDbContextFactory<StudioDbContext> dbContextFactory;

    public ReaderProfileRepository(IDbContextFactory<StudioDbContext> dbContextFactory)
    {
        this.dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlyList<SavedReaderProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using StudioDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStorageAsync(dbContext, cancellationToken).ConfigureAwait(false);

        return await dbContext.ReaderProfiles
            .AsNoTracking()
            .OrderBy(static reader => reader.CreatedAtUtc)
            .ThenBy(static reader => reader.Name)
            .Select(static reader => new SavedReaderProfile(new ReaderProfile
            {
                Id = reader.Id,
                Name = reader.Name,
                Host = reader.Host,
                Port = reader.Port,
                EnableImpinjExtensions = reader.EnableImpinjExtensions,
            }, reader.IsEnabled))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveAsync(ReaderProfile profile, bool isEnabled = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using StudioDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStorageAsync(dbContext, cancellationToken).ConfigureAwait(false);

        ReaderProfileEntity? entity = await dbContext.ReaderProfiles
            .FirstOrDefaultAsync(reader => reader.Id == profile.Id, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new ReaderProfileEntity
            {
                Id = profile.Id,
                CreatedAtUtc = DateTime.UtcNow,
            };
            dbContext.ReaderProfiles.Add(entity);
        }

        entity.Name = profile.Name;
        entity.Host = profile.Host;
        entity.Port = profile.Port;
        entity.EnableImpinjExtensions = profile.EnableImpinjExtensions;
        entity.IsEnabled = isEnabled;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(Guid id, bool isEnabled, CancellationToken cancellationToken = default)
    {
        await using StudioDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStorageAsync(dbContext, cancellationToken).ConfigureAwait(false);

        ReaderProfileEntity? entity = await dbContext.ReaderProfiles
            .FirstOrDefaultAsync(reader => reader.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return;
        }

        entity.IsEnabled = isEnabled;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using StudioDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStorageAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await dbContext.ReaderProfiles
            .Where(reader => reader.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureStorageAsync(StudioDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        var connection = (SqliteConnection)dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(ReaderProfiles);";
        bool hasIsEnabled = false;
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "IsEnabled", StringComparison.OrdinalIgnoreCase))
                {
                    hasIsEnabled = true;
                    break;
                }
            }
        }

        if (!hasIsEnabled)
        {
            await using SqliteCommand alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE ReaderProfiles ADD COLUMN IsEnabled INTEGER NOT NULL DEFAULT 1;";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
