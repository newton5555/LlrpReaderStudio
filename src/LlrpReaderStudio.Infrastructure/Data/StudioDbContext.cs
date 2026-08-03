using LlrpReaderStudio.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LlrpReaderStudio.Infrastructure.Data;

public sealed class StudioDbContext : DbContext
{
    public StudioDbContext(DbContextOptions<StudioDbContext> options)
        : base(options)
    {
    }

    public DbSet<ReaderProfileEntity> ReaderProfiles => Set<ReaderProfileEntity>();
    public DbSet<ReaderPresetEntity> ReaderPresets => Set<ReaderPresetEntity>();
    public DbSet<InventoryPresetEntity> InventoryPresets => Set<InventoryPresetEntity>();
    public DbSet<TagListEntity> TagLists => Set<TagListEntity>();
    public DbSet<TagListEntryEntity> TagListEntries => Set<TagListEntryEntity>();
    public DbSet<InventoryRunEntity> InventoryRuns => Set<InventoryRunEntity>();
    public DbSet<AppSettingEntity> AppSettings => Set<AppSettingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TagListEntryEntity>()
            .HasOne(e => e.TagList)
            .WithMany(l => l.Entries)
            .HasForeignKey(e => e.TagListId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TagListEntryEntity>()
            .HasIndex(e => new { e.TagListId, e.EpcHex })
            .IsUnique();
    }
}
