using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SoteroMap.API.Data;

public static class ExtendedSchemaInitializer
{
    public static async Task EnsureAsync(AppDbContext context)
    {
        await context.Database.OpenConnectionAsync();

        try
        {
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS SyncedBuildings (
                    Id INTEGER NOT NULL CONSTRAINT PK_SyncedBuildings PRIMARY KEY AUTOINCREMENT,
                    ExternalId TEXT NOT NULL,
                    Campus TEXT NOT NULL,
                    Slug TEXT NOT NULL,
                    DisplayName TEXT NOT NULL,
                    ShortName TEXT NOT NULL,
                    RealName TEXT NOT NULL,
                    Type TEXT NOT NULL,
                    ResponsibleArea TEXT NOT NULL,
                    Notes TEXT NOT NULL,
                    SourceId TEXT NOT NULL,
                    CentroidLatitude REAL NULL,
                    CentroidLongitude REAL NULL,
                    HasInteriorMap INTEGER NOT NULL,
                    HasInventory INTEGER NOT NULL,
                    MappingStatus TEXT NOT NULL,
                    InventoryStatus TEXT NOT NULL,
                    OperationalNotes TEXT NOT NULL,
                    TechnicalNotes TEXT NOT NULL,
                    LastUpdate TEXT NOT NULL,
                    FloorsJson TEXT NOT NULL,
                    FloorSummariesJson TEXT NOT NULL,
                    TagsJson TEXT NOT NULL,
                    ContactsJson TEXT NOT NULL,
                    SyncedAtUtc TEXT NOT NULL
                );
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_SyncedBuildings_ExternalId
                ON SyncedBuildings (ExternalId);
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS SyncedRooms (
                    Id INTEGER NOT NULL CONSTRAINT PK_SyncedRooms PRIMARY KEY AUTOINCREMENT,
                    ExternalId TEXT NOT NULL,
                    SyncedBuildingId INTEGER NOT NULL,
                    BuildingExternalId TEXT NOT NULL,
                    Floor INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    ShortName TEXT NOT NULL,
                    Type TEXT NOT NULL,
                    Sector TEXT NOT NULL,
                    Unit TEXT NOT NULL,
                    Service TEXT NOT NULL,
                    IsMapped INTEGER NOT NULL,
                    GeometryJson TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    Capacity INTEGER NULL,
                    DevicesCount INTEGER NOT NULL,
                    ResponsibleArea TEXT NOT NULL,
                    ResponsiblePerson TEXT NOT NULL,
                    Notes TEXT NOT NULL,
                    SyncedAtUtc TEXT NOT NULL,
                    CONSTRAINT FK_SyncedRooms_SyncedBuildings_SyncedBuildingId
                        FOREIGN KEY (SyncedBuildingId) REFERENCES SyncedBuildings (Id) ON DELETE CASCADE
                );
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_SyncedRooms_ExternalId
                ON SyncedRooms (ExternalId);
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_SyncedRooms_SyncedBuildingId_Floor
                ON SyncedRooms (SyncedBuildingId, Floor);
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS SyncedEquipments (
                    Id INTEGER NOT NULL CONSTRAINT PK_SyncedEquipments PRIMARY KEY AUTOINCREMENT,
                    ExternalId TEXT NOT NULL,
                    SyncedBuildingId INTEGER NOT NULL,
                    SyncedRoomId INTEGER NULL,
                    BuildingExternalId TEXT NOT NULL,
                    RoomExternalId TEXT NOT NULL,
                    Floor INTEGER NULL,
                    Type TEXT NOT NULL,
                    Subtype TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    InventoryCode TEXT NOT NULL,
                    SerialNumber TEXT NOT NULL,
                    Brand TEXT NOT NULL,
                    Model TEXT NOT NULL,
                    IpAddress TEXT NOT NULL,
                    MacAddress TEXT NOT NULL,
                    AssignedTo TEXT NOT NULL,
                    ResponsiblePerson TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    NetworkStatus TEXT NOT NULL,
                    LastSeen TEXT NOT NULL,
                    PurchaseDate TEXT NOT NULL,
                    Notes TEXT NOT NULL,
                    HistoryJson TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    SyncedAtUtc TEXT NOT NULL,
                    CONSTRAINT FK_SyncedEquipments_SyncedBuildings_SyncedBuildingId
                        FOREIGN KEY (SyncedBuildingId) REFERENCES SyncedBuildings (Id) ON DELETE CASCADE,
                    CONSTRAINT FK_SyncedEquipments_SyncedRooms_SyncedRoomId
                        FOREIGN KEY (SyncedRoomId) REFERENCES SyncedRooms (Id) ON DELETE SET NULL
                );
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_SyncedEquipments_ExternalId
                ON SyncedEquipments (ExternalId);
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_SyncedEquipments_BuildingExternalId
                ON SyncedEquipments (BuildingExternalId);
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_SyncedEquipments_RoomExternalId
                ON SyncedEquipments (RoomExternalId);
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS ImportedInventoryItems (
                    Id INTEGER NOT NULL CONSTRAINT PK_ImportedInventoryItems PRIMARY KEY AUTOINCREMENT,
                    RowNumber INTEGER NOT NULL,
                    ItemNumber TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    Lot TEXT NOT NULL,
                    InstallDate TEXT NOT NULL,
                    UnitOrDepartment TEXT NOT NULL,
                    OrganizationalUnit TEXT NOT NULL,
                    ResponsibleUser TEXT NOT NULL,
                    Run TEXT NOT NULL,
                    Email TEXT NOT NULL,
                    JobTitle TEXT NOT NULL,
                    IpAddress TEXT NOT NULL,
                    AnnexPhone TEXT NOT NULL,
                    ReplacedEquipment TEXT NOT NULL,
                    TicketMda TEXT NOT NULL,
                    Installer TEXT NOT NULL,
                    Observation TEXT NOT NULL,
                    Rut TEXT NOT NULL,
                    InventoryDate TEXT NOT NULL,
                    InferredCategory TEXT NOT NULL,
                    InferredStatus TEXT NOT NULL,
                    SourceFile TEXT NOT NULL,
                    ImportedAtUtc TEXT NOT NULL
                );
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_ImportedInventoryItems_SourceFile_RowNumber
                ON ImportedInventoryItems (SourceFile, RowNumber);
                """);

            await EnsureColumnAsync(context, "ImportedInventoryItems", "MatchedSyncedBuildingId", "INTEGER NULL");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "MatchedSyncedRoomId", "INTEGER NULL");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "MatchedBuildingExternalId", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "MatchedRoomExternalId", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "MatchConfidence", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "MatchNotes", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "SerialNumber", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "MacAddress", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "AssignedBuildingExternalId", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "AssignedRoomExternalId", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "AssignedFloor", "INTEGER NULL");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "AssignmentNotes", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "ImportedInventoryItems", "AssignmentUpdatedAtUtc", "TEXT NULL");
            await EnsureColumnAsync(context, "SyncedBuildings", "ManualCampus", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "SyncedBuildings", "ManualDisplayName", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "SyncedBuildings", "ManualFloorsJson", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "SyncedRooms", "ManualName", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(context, "SyncedRooms", "ManualFloor", "INTEGER NULL");

            await context.Database.ExecuteSqlRawAsync("""
                UPDATE ImportedInventoryItems
                SET SerialNumber = Lot
                WHERE IFNULL(SerialNumber, '') = '' AND IFNULL(Lot, '') <> '';
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_ImportedInventoryItems_AssignedBuildingExternalId
                ON ImportedInventoryItems (AssignedBuildingExternalId);
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_ImportedInventoryItems_AssignedRoomExternalId
                ON ImportedInventoryItems (AssignedRoomExternalId);
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS InventoryAliasRules (
                    Id INTEGER NOT NULL CONSTRAINT PK_InventoryAliasRules PRIMARY KEY AUTOINCREMENT,
                    SourceText TEXT NOT NULL,
                    NormalizedSourceText TEXT NOT NULL,
                    TargetBuildingExternalId TEXT NOT NULL,
                    TargetRoomExternalId TEXT NOT NULL,
                    IsEnabled INTEGER NOT NULL,
                    Notes TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_InventoryAliasRules_NormalizedSourceText
                ON InventoryAliasRules (NormalizedSourceText);
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS AuthUsers (
                    Id INTEGER NOT NULL CONSTRAINT PK_AuthUsers PRIMARY KEY AUTOINCREMENT,
                    Username TEXT NOT NULL,
                    NormalizedUsername TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    IsActive INTEGER NOT NULL,
                    FailedLoginAttempts INTEGER NOT NULL,
                    LockedUntilUtc TEXT NULL,
                    LastLoginAtUtc TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_AuthUsers_NormalizedUsername
                ON AuthUsers (NormalizedUsername);
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS AuditLogEntries (
                    Id INTEGER NOT NULL CONSTRAINT PK_AuditLogEntries PRIMARY KEY AUTOINCREMENT,
                    BuildingExternalId TEXT NOT NULL,
                    EntityType TEXT NOT NULL,
                    EntityId TEXT NOT NULL,
                    ActionType TEXT NOT NULL,
                    Summary TEXT NOT NULL,
                    Details TEXT NOT NULL,
                    ChangedByUsername TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );
                """);

            await context.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_AuditLogEntries_BuildingExternalId_CreatedAtUtc
                ON AuditLogEntries (BuildingExternalId, CreatedAtUtc);
                """);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureColumnAsync(AppDbContext context, string tableName, string columnName, string definition)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var exists = false;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader["name"]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            await context.Database.ExecuteSqlRawAsync($"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};");
        }
    }
}
