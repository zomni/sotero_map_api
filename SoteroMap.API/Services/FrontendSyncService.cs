using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;

namespace SoteroMap.API.Services;

public class FrontendSyncService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FrontendSyncService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FrontendSyncService(
        AppDbContext context,
        IConfiguration configuration,
        ILogger<FrontendSyncService> logger)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<FrontendSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var dataRoot = ResolveDataRoot();
        var catalogPath = Path.Combine(dataRoot, "sotero_buildings_catalog.json");

        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException("No se encontró sotero_buildings_catalog.json en la ruta configurada.", catalogPath);
        }

        var catalog = await ReadJsonAsync<BuildingCatalog>(catalogPath, cancellationToken) ?? new BuildingCatalog();
        var buildings = catalog.Buildings ?? [];
        var syncedAt = DateTime.UtcNow;
        var existingBuildingOverrides = await _context.SyncedBuildings
            .AsNoTracking()
            .ToDictionaryAsync(
                b => b.ExternalId,
                b => new BuildingOverrideSnapshot
                {
                    ManualCampus = b.ManualCampus,
                    ManualDisplayName = b.ManualDisplayName,
                    ManualFloorsJson = b.ManualFloorsJson
                },
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);
        var existingRoomOverrides = await _context.SyncedRooms
            .AsNoTracking()
            .ToDictionaryAsync(
                r => r.ExternalId,
                r => new RoomOverrideSnapshot
                {
                    ManualName = r.ManualName,
                    ManualFloor = r.ManualFloor
                },
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);

        _logger.LogInformation("Iniciando sincronización desde frontend data root: {DataRoot}", dataRoot);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        _context.SyncedEquipments.RemoveRange(await _context.SyncedEquipments.ToListAsync(cancellationToken));
        _context.SyncedRooms.RemoveRange(await _context.SyncedRooms.ToListAsync(cancellationToken));
        _context.SyncedBuildings.RemoveRange(await _context.SyncedBuildings.ToListAsync(cancellationToken));
        await _context.SaveChangesAsync(cancellationToken);

        var syncedBuildings = new List<SyncedBuilding>();
        var syncedRooms = new List<SyncedRoom>();
        var syncedEquipments = new List<SyncedEquipment>();

        foreach (var building in buildings)
        {
            var detailPath = Path.Combine(dataRoot, "interiors", building.Id, "building_detail.json");
            var detail = File.Exists(detailPath)
                ? await ReadJsonAsync<BuildingDetail>(detailPath, cancellationToken)
                : null;

            var syncedBuilding = new SyncedBuilding
            {
                ExternalId = building.Id,
                Campus = "sotero",
                ManualCampus = existingBuildingOverrides.GetValueOrDefault(building.Id)?.ManualCampus ?? string.Empty,
                Slug = building.Slug ?? string.Empty,
                DisplayName = building.DisplayName ?? building.RealName ?? building.Id,
                ManualDisplayName = existingBuildingOverrides.GetValueOrDefault(building.Id)?.ManualDisplayName ?? string.Empty,
                ShortName = building.ShortName ?? string.Empty,
                RealName = building.RealName ?? building.DisplayName ?? string.Empty,
                Type = detail?.Type ?? building.Type ?? string.Empty,
                ResponsibleArea = detail?.ResponsibleArea ?? building.ResponsibleArea ?? string.Empty,
                Notes = building.Notes ?? string.Empty,
                SourceId = building.SourceId ?? string.Empty,
                CentroidLongitude = building.Centroid?.ElementAtOrDefault(0),
                CentroidLatitude = building.Centroid?.ElementAtOrDefault(1),
                HasInteriorMap = detail?.HasInteriorMap ?? building.HasInteriorMap,
                HasInventory = detail?.HasInventory ?? building.HasInventory,
                MappingStatus = detail?.MappingStatus ?? string.Empty,
                InventoryStatus = detail?.InventoryStatus ?? string.Empty,
                OperationalNotes = detail?.OperationalNotes ?? string.Empty,
                TechnicalNotes = detail?.TechnicalNotes ?? string.Empty,
                LastUpdate = detail?.LastUpdate ?? string.Empty,
                FloorsJson = SerializeJson(detail?.Floors ?? building.Floors ?? []),
                ManualFloorsJson = existingBuildingOverrides.GetValueOrDefault(building.Id)?.ManualFloorsJson ?? string.Empty,
                FloorSummariesJson = SerializeJson(detail?.FloorSummaries ?? []),
                TagsJson = SerializeJson(detail?.Tags ?? []),
                ContactsJson = SerializeJson(detail?.Contacts ?? []),
                SyncedAtUtc = syncedAt
            };

            syncedBuildings.Add(syncedBuilding);
        }

        _context.SyncedBuildings.AddRange(syncedBuildings);
        await _context.SaveChangesAsync(cancellationToken);

        var buildingMap = syncedBuildings.ToDictionary(b => b.ExternalId, StringComparer.OrdinalIgnoreCase);

        foreach (var building in buildings)
        {
            if (!buildingMap.TryGetValue(building.Id, out var syncedBuilding))
            {
                continue;
            }

            var roomsDir = Path.Combine(dataRoot, "interiors", building.Id);
            if (!Directory.Exists(roomsDir))
            {
                continue;
            }

            var roomFiles = Directory
                .EnumerateFiles(roomsDir, "floor_*_rooms.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var roomFile in roomFiles)
            {
                var roomDocument = await ReadJsonAsync<RoomFileDocument>(roomFile, cancellationToken);
                if (roomDocument?.Rooms == null)
                {
                    continue;
                }

                foreach (var room in roomDocument.Rooms)
                {
                    syncedRooms.Add(new SyncedRoom
                    {
                        ExternalId = room.RoomId ?? string.Empty,
                        SyncedBuildingId = syncedBuilding.Id,
                        BuildingExternalId = room.BuildingId ?? building.Id,
                        Floor = room.Floor,
                        ManualFloor = existingRoomOverrides.GetValueOrDefault(room.RoomId ?? string.Empty)?.ManualFloor,
                        Name = room.Name ?? room.RoomId ?? string.Empty,
                        ManualName = existingRoomOverrides.GetValueOrDefault(room.RoomId ?? string.Empty)?.ManualName ?? string.Empty,
                        ShortName = room.ShortName ?? string.Empty,
                        Type = room.Type ?? string.Empty,
                        Sector = room.Sector ?? string.Empty,
                        Unit = room.Unit ?? string.Empty,
                        Service = room.Service ?? string.Empty,
                        IsMapped = room.IsMapped,
                        GeometryJson = SerializeJson(room.Geometry),
                        Status = room.Status ?? string.Empty,
                        Capacity = room.Capacity,
                        DevicesCount = room.DevicesCount,
                        ResponsibleArea = room.ResponsibleArea ?? string.Empty,
                        ResponsiblePerson = room.ResponsiblePerson ?? string.Empty,
                        Notes = room.Notes ?? string.Empty,
                        SyncedAtUtc = syncedAt
                    });
                }
            }
        }

        _context.SyncedRooms.AddRange(syncedRooms);
        await _context.SaveChangesAsync(cancellationToken);

        var roomMap = syncedRooms.ToDictionary(r => r.ExternalId, StringComparer.OrdinalIgnoreCase);

        foreach (var building in buildings)
        {
            if (!buildingMap.TryGetValue(building.Id, out var syncedBuilding))
            {
                continue;
            }

            var devicesPath = Path.Combine(dataRoot, "interiors", building.Id, "devices.json");
            if (!File.Exists(devicesPath))
            {
                continue;
            }

            var deviceDocument = await ReadJsonAsync<DeviceFileDocument>(devicesPath, cancellationToken);
            if (deviceDocument?.Devices == null)
            {
                continue;
            }

            foreach (var device in deviceDocument.Devices)
            {
                roomMap.TryGetValue(device.RoomId ?? string.Empty, out var syncedRoom);

                syncedEquipments.Add(new SyncedEquipment
                {
                    ExternalId = device.DeviceId ?? string.Empty,
                    SyncedBuildingId = syncedBuilding.Id,
                    SyncedRoomId = syncedRoom?.Id,
                    BuildingExternalId = device.BuildingId ?? building.Id,
                    RoomExternalId = device.RoomId ?? string.Empty,
                    Floor = syncedRoom?.Floor,
                    Type = device.Type ?? string.Empty,
                    Subtype = device.Subtype ?? string.Empty,
                    Name = device.Name ?? device.DeviceId ?? string.Empty,
                    InventoryCode = device.InventoryCode ?? string.Empty,
                    SerialNumber = device.SerialNumber ?? string.Empty,
                    Brand = device.Brand ?? string.Empty,
                    Model = device.Model ?? string.Empty,
                    IpAddress = device.Ip ?? string.Empty,
                    MacAddress = device.Mac ?? string.Empty,
                    AssignedTo = device.AssignedTo ?? string.Empty,
                    ResponsiblePerson = device.ResponsiblePerson ?? string.Empty,
                    Status = device.Status ?? string.Empty,
                    NetworkStatus = device.NetworkStatus ?? string.Empty,
                    LastSeen = device.LastSeen ?? string.Empty,
                    PurchaseDate = device.PurchaseDate ?? string.Empty,
                    Notes = device.Notes ?? string.Empty,
                    HistoryJson = SerializeJson(device.History ?? []),
                    Source = "frontend_sync",
                    SyncedAtUtc = syncedAt
                });
            }
        }

        _context.SyncedEquipments.AddRange(syncedEquipments);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new FrontendSyncResult
        {
            DataRoot = dataRoot,
            BuildingsCount = syncedBuildings.Count,
            RoomsCount = syncedRooms.Count,
            EquipmentsCount = syncedEquipments.Count,
            SyncedAtUtc = syncedAt
        };
    }

    public async Task<FrontendSyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var buildings = await _context.SyncedBuildings.CountAsync(cancellationToken);
        var rooms = await _context.SyncedRooms.CountAsync(cancellationToken);
        var equipments = await _context.SyncedEquipments.CountAsync(cancellationToken);
        var latestSync = await _context.SyncedBuildings
            .OrderByDescending(b => b.SyncedAtUtc)
            .Select(b => (DateTime?)b.SyncedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return new FrontendSyncStatus
        {
            DataRoot = ResolveDataRoot(),
            BuildingsCount = buildings,
            RoomsCount = rooms,
            EquipmentsCount = equipments,
            LastSyncUtc = latestSync
        };
    }

    private string ResolveDataRoot()
    {
        var configured = _configuration["FrontendDataRoot"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sotero_map", "src", "data"));
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static string SerializeJson<T>(T value)
    {
        return JsonSerializer.Serialize(value ?? Activator.CreateInstance<T>());
    }

    public sealed class FrontendSyncResult
    {
        public string DataRoot { get; set; } = string.Empty;
        public int BuildingsCount { get; set; }
        public int RoomsCount { get; set; }
        public int EquipmentsCount { get; set; }
        public DateTime SyncedAtUtc { get; set; }
    }

    public sealed class FrontendSyncStatus
    {
        public string DataRoot { get; set; } = string.Empty;
        public int BuildingsCount { get; set; }
        public int RoomsCount { get; set; }
        public int EquipmentsCount { get; set; }
        public DateTime? LastSyncUtc { get; set; }
    }

    private sealed class BuildingCatalog
    {
        public List<BuildingCatalogEntry>? Buildings { get; set; }
    }

    private sealed class BuildingCatalogEntry
    {
        public string Id { get; set; } = string.Empty;
        public string? Slug { get; set; }
        public string? DisplayName { get; set; }
        public string? ShortName { get; set; }
        public string? RealName { get; set; }
        public string? Type { get; set; }
        public List<int>? Floors { get; set; }
        public bool HasInteriorMap { get; set; }
        public bool HasInventory { get; set; }
        public string? ResponsibleArea { get; set; }
        public string? Notes { get; set; }
        public string? SourceId { get; set; }
        public List<double>? Centroid { get; set; }
    }

    private sealed class BuildingDetail
    {
        public string? Type { get; set; }
        public string? ResponsibleArea { get; set; }
        public List<int>? Floors { get; set; }
        public bool HasInteriorMap { get; set; }
        public bool HasInventory { get; set; }
        public string? InventoryStatus { get; set; }
        public string? MappingStatus { get; set; }
        public List<object>? FloorSummaries { get; set; }
        public string? OperationalNotes { get; set; }
        public string? TechnicalNotes { get; set; }
        public string? LastUpdate { get; set; }
        public List<object>? Contacts { get; set; }
        public List<string>? Tags { get; set; }
    }

    private sealed class RoomFileDocument
    {
        public List<RoomEntry>? Rooms { get; set; }
    }

    private sealed class RoomEntry
    {
        public string? RoomId { get; set; }
        public string? Name { get; set; }
        public string? ShortName { get; set; }
        public string? Type { get; set; }
        public int Floor { get; set; }
        public string? BuildingId { get; set; }
        public string? Sector { get; set; }
        public string? Unit { get; set; }
        public string? Service { get; set; }
        public bool IsMapped { get; set; }
        public object? Geometry { get; set; }
        public string? Status { get; set; }
        public int? Capacity { get; set; }
        public int DevicesCount { get; set; }
        public string? ResponsibleArea { get; set; }
        public string? ResponsiblePerson { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class DeviceFileDocument
    {
        public List<DeviceEntry>? Devices { get; set; }
    }

    private sealed class DeviceEntry
    {
        public string? DeviceId { get; set; }
        public string? RoomId { get; set; }
        public string? BuildingId { get; set; }
        public string? Type { get; set; }
        public string? Subtype { get; set; }
        public string? Name { get; set; }
        public string? InventoryCode { get; set; }
        public string? SerialNumber { get; set; }
        public string? Brand { get; set; }
        public string? Model { get; set; }
        public string? Ip { get; set; }
        public string? Mac { get; set; }
        public string? AssignedTo { get; set; }
        public string? ResponsiblePerson { get; set; }
        public string? Status { get; set; }
        public string? NetworkStatus { get; set; }
        public string? LastSeen { get; set; }
        public string? PurchaseDate { get; set; }
        public string? Notes { get; set; }
        public List<object>? History { get; set; }
    }

    private sealed class BuildingOverrideSnapshot
    {
        public string ManualCampus { get; set; } = string.Empty;
        public string ManualDisplayName { get; set; } = string.Empty;
        public string ManualFloorsJson { get; set; } = string.Empty;
    }

    private sealed class RoomOverrideSnapshot
    {
        public string ManualName { get; set; } = string.Empty;
        public int? ManualFloor { get; set; }
    }
}
