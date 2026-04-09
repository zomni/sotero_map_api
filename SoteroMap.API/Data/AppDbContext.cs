using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Models;

namespace SoteroMap.API.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Equipment> Equipments => Set<Equipment>();
    public DbSet<SyncedBuilding> SyncedBuildings => Set<SyncedBuilding>();
    public DbSet<SyncedRoom> SyncedRooms => Set<SyncedRoom>();
    public DbSet<SyncedEquipment> SyncedEquipments => Set<SyncedEquipment>();
    public DbSet<ImportedInventoryItem> ImportedInventoryItems => Set<ImportedInventoryItem>();
    public DbSet<InventoryAliasRule> InventoryAliasRules => Set<InventoryAliasRule>();
    public DbSet<AuthUser> AuthUsers => Set<AuthUser>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Location>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Campus).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Type).HasMaxLength(50);
            entity.Property(e => e.Floor).HasMaxLength(10);
        });

        modelBuilder.Entity<Equipment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.Property(e => e.SerialNumber).HasMaxLength(100);
            entity.Property(e => e.Status).HasMaxLength(50);

            entity.HasOne(e => e.Location)
                  .WithMany(l => l.Equipments)
                  .HasForeignKey(e => e.LocationId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SyncedBuilding>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExternalId).IsUnique();
            entity.Property(e => e.ExternalId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Campus).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ManualCampus).HasMaxLength(50);
            entity.Property(e => e.Slug).HasMaxLength(200);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ManualDisplayName).HasMaxLength(200);
            entity.Property(e => e.ShortName).HasMaxLength(100);
            entity.Property(e => e.RealName).HasMaxLength(200);
            entity.Property(e => e.Type).HasMaxLength(100);
            entity.Property(e => e.ResponsibleArea).HasMaxLength(200);
            entity.Property(e => e.SourceId).HasMaxLength(200);
            entity.Property(e => e.ManualFloorsJson).HasMaxLength(500);
        });

        modelBuilder.Entity<SyncedRoom>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExternalId).IsUnique();
            entity.HasIndex(e => new { e.SyncedBuildingId, e.Floor });
            entity.Property(e => e.ExternalId).IsRequired().HasMaxLength(120);
            entity.Property(e => e.BuildingExternalId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ManualName).HasMaxLength(200);
            entity.Property(e => e.ShortName).HasMaxLength(100);
            entity.Property(e => e.Type).HasMaxLength(100);
            entity.Property(e => e.Sector).HasMaxLength(100);
            entity.Property(e => e.Unit).HasMaxLength(100);
            entity.Property(e => e.Service).HasMaxLength(100);
            entity.Property(e => e.Status).HasMaxLength(50);
            entity.Property(e => e.ResponsibleArea).HasMaxLength(200);
            entity.Property(e => e.ResponsiblePerson).HasMaxLength(200);

            entity.HasOne(e => e.SyncedBuilding)
                .WithMany(b => b.Rooms)
                .HasForeignKey(e => e.SyncedBuildingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SyncedEquipment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExternalId).IsUnique();
            entity.HasIndex(e => e.BuildingExternalId);
            entity.HasIndex(e => e.RoomExternalId);
            entity.Property(e => e.ExternalId).IsRequired().HasMaxLength(120);
            entity.Property(e => e.BuildingExternalId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.RoomExternalId).HasMaxLength(120);
            entity.Property(e => e.Type).HasMaxLength(50);
            entity.Property(e => e.Subtype).HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.InventoryCode).HasMaxLength(100);
            entity.Property(e => e.SerialNumber).HasMaxLength(100);
            entity.Property(e => e.Brand).HasMaxLength(100);
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.IpAddress).HasMaxLength(100);
            entity.Property(e => e.MacAddress).HasMaxLength(100);
            entity.Property(e => e.AssignedTo).HasMaxLength(200);
            entity.Property(e => e.ResponsiblePerson).HasMaxLength(200);
            entity.Property(e => e.Status).HasMaxLength(50);
            entity.Property(e => e.NetworkStatus).HasMaxLength(50);
            entity.Property(e => e.LastSeen).HasMaxLength(50);
            entity.Property(e => e.PurchaseDate).HasMaxLength(50);
            entity.Property(e => e.Source).HasMaxLength(50);

            entity.HasOne(e => e.SyncedBuilding)
                .WithMany(b => b.Equipments)
                .HasForeignKey(e => e.SyncedBuildingId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SyncedRoom)
                .WithMany(r => r.Equipments)
                .HasForeignKey(e => e.SyncedRoomId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ImportedInventoryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SourceFile, e.RowNumber }).IsUnique();
            entity.HasIndex(e => e.AssignedBuildingExternalId);
            entity.HasIndex(e => e.AssignedRoomExternalId);
            entity.Property(e => e.ItemNumber).HasMaxLength(50);
            entity.Property(e => e.SerialNumber).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Lot).HasMaxLength(100);
            entity.Property(e => e.InstallDate).HasMaxLength(50);
            entity.Property(e => e.UnitOrDepartment).HasMaxLength(200);
            entity.Property(e => e.OrganizationalUnit).HasMaxLength(200);
            entity.Property(e => e.ResponsibleUser).HasMaxLength(200);
            entity.Property(e => e.Run).HasMaxLength(50);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.JobTitle).HasMaxLength(200);
            entity.Property(e => e.IpAddress).HasMaxLength(100);
            entity.Property(e => e.MacAddress).HasMaxLength(100);
            entity.Property(e => e.AnnexPhone).HasMaxLength(100);
            entity.Property(e => e.ReplacedEquipment).HasMaxLength(200);
            entity.Property(e => e.TicketMda).HasMaxLength(100);
            entity.Property(e => e.Installer).HasMaxLength(200);
            entity.Property(e => e.Observation).HasMaxLength(500);
            entity.Property(e => e.Rut).HasMaxLength(50);
            entity.Property(e => e.InventoryDate).HasMaxLength(50);
            entity.Property(e => e.InferredCategory).HasMaxLength(50);
            entity.Property(e => e.InferredStatus).HasMaxLength(50);
            entity.Property(e => e.MatchedBuildingExternalId).HasMaxLength(100);
            entity.Property(e => e.MatchedRoomExternalId).HasMaxLength(120);
            entity.Property(e => e.MatchConfidence).HasMaxLength(50);
            entity.Property(e => e.MatchNotes).HasMaxLength(500);
            entity.Property(e => e.AssignedBuildingExternalId).HasMaxLength(100);
            entity.Property(e => e.AssignedRoomExternalId).HasMaxLength(120);
            entity.Property(e => e.AssignmentNotes).HasMaxLength(500);
            entity.Property(e => e.SourceFile).HasMaxLength(260);
        });

        modelBuilder.Entity<InventoryAliasRule>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.NormalizedSourceText).IsUnique();
            entity.Property(e => e.SourceText).IsRequired().HasMaxLength(200);
            entity.Property(e => e.NormalizedSourceText).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TargetBuildingExternalId).HasMaxLength(100);
            entity.Property(e => e.TargetRoomExternalId).HasMaxLength(120);
            entity.Property(e => e.Notes).HasMaxLength(500);
        });

        modelBuilder.Entity<AuthUser>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.NormalizedUsername).IsUnique();
            entity.Property(e => e.Username).IsRequired().HasMaxLength(100);
            entity.Property(e => e.NormalizedUsername).IsRequired().HasMaxLength(100);
            entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Role).IsRequired().HasMaxLength(20);
        });

        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.BuildingExternalId, e.CreatedAtUtc });
            entity.Property(e => e.BuildingExternalId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.EntityId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ActionType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Summary).IsRequired().HasMaxLength(300);
            entity.Property(e => e.Details).HasMaxLength(1000);
            entity.Property(e => e.ChangedByUsername).IsRequired().HasMaxLength(100);
        });
    }
}
