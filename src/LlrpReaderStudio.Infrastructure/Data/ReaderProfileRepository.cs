using LlrpReaderStudio.Core;
using LlrpReaderStudio.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace LlrpReaderStudio.Infrastructure.Data;

public sealed record SavedReaderProfile(
    ReaderProfile Profile,
    bool IsEnabled,
    DateTime? LastCheckedAtUtc = null,
    string? LastError = null,
    string? Model = null,
    string? Firmware = null);

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
                LlrpVersion = (LlrpProtocolVersionOption)reader.LlrpVersion,
            }, reader.IsEnabled, reader.LastCheckedAtUtc, reader.LastError, reader.Model, reader.Firmware))
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
        entity.LlrpVersion = (int)profile.LlrpVersion;
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

    /// <summary>
    /// Persists the latest connectivity check outcome (timestamp, identity and error) so the
    /// device list can show the last known state without connecting.
    /// </summary>
    public async Task UpdateStatusAsync(
        Guid id,
        DateTime? lastCheckedAtUtc,
        string? model,
        string? firmware,
        string? error,
        CancellationToken cancellationToken = default)
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

        entity.LastCheckedAtUtc = lastCheckedAtUtc;
        entity.Model = model;
        entity.Firmware = firmware;
        entity.LastError = error;
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

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(ReaderProfiles);";
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    columns.Add(reader.GetString(1));
                }
            }
        }

        // Idempotent column migrations for the device list state (last check outcome / identity).
        await AddColumnIfMissingAsync(connection, columns, "IsEnabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await AddColumnIfMissingAsync(connection, columns, "LastCheckedAtUtc", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, columns, "LastError", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, columns, "Model", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, columns, "Firmware", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, columns, "LlrpVersion", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        HashSet<string> columns,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        if (columns.Contains(columnName))
        {
            return;
        }

        await using SqliteCommand alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE ReaderProfiles ADD COLUMN {columnName} {columnDefinition};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        columns.Add(columnName);
    }
}
