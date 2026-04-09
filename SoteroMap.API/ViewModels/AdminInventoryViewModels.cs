using SoteroMap.API.Models;

namespace SoteroMap.API.ViewModels;

public class AdminInventoryListViewModel
{
    public string Search { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string AssignmentFilter { get; set; } = "pending";
    public string BuildingExternalId { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 30;
    public int TotalFilteredItems { get; set; }
    public IReadOnlyList<ImportedInventoryItem> Items { get; set; } = [];
    public IReadOnlyList<SyncedBuilding> Buildings { get; set; } = [];
    public int TotalItems { get; set; }
    public int AssignedItems { get; set; }
    public int PendingItems { get; set; }
    public int SuggestedItems { get; set; }
}

public class EditInventoryItemViewModel
{
    public ImportedInventoryItem Item { get; set; } = null!;
    public IReadOnlyList<SyncedBuilding> Buildings { get; set; } = [];
    public IReadOnlyList<SyncedRoom> Rooms { get; set; } = [];
}

public class AdminLocationsViewModel
{
    public string Search { get; set; } = string.Empty;
    public string Campus { get; set; } = string.Empty;
    public string Floor { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 30;
    public int TotalFilteredLocations { get; set; }
    public IReadOnlyList<AdminLocationRowViewModel> Locations { get; set; } = [];
    public int TotalBuildings { get; set; }
    public int BuildingsWithInteriorMap { get; set; }
    public int TotalRooms { get; set; }
    public int AssignedInventoryItems { get; set; }
}

public class AdminLocationRowViewModel
{
    public string ExternalId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Campus { get; set; } = string.Empty;
    public int DefaultMapFloor { get; set; }
    public bool HasManualOverride { get; set; }
    public string Type { get; set; } = string.Empty;
    public bool HasInteriorMap { get; set; }
    public string MappingStatus { get; set; } = string.Empty;
    public string InventoryStatus { get; set; } = string.Empty;
    public string AvailableFloors { get; set; } = string.Empty;
    public int RoomsCount { get; set; }
    public int AssignedInventoryCount { get; set; }
    public int SuggestedInventoryCount { get; set; }
    public string Coordinates { get; set; } = string.Empty;
}

public class AdminActivityViewModel
{
    public string BuildingExternalId { get; set; } = string.Empty;
    public string ChangedByUsername { get; set; } = string.Empty;
    public IReadOnlyList<ActivityLogListItemViewModel> Items { get; set; } = [];
}

public class EditSyncedBuildingViewModel
{
    public SyncedBuilding Building { get; set; } = null!;
    public IReadOnlyList<SyncedRoom> Rooms { get; set; } = [];
}

public class EditSyncedRoomViewModel
{
    public SyncedRoom Room { get; set; } = null!;
    public SyncedBuilding Building { get; set; } = null!;
}
