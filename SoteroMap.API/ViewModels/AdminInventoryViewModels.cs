using SoteroMap.API.Models;

namespace SoteroMap.API.ViewModels;

public class AdminInventoryListViewModel
{
    public string Search { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string AssignmentFilter { get; set; } = "pending";
    public string BuildingExternalId { get; set; } = string.Empty;
    public string SortBy { get; set; } = "row";
    public string SortDirection { get; set; } = "asc";
    public string InconsistencyType { get; set; } = string.Empty;
    public bool OnlyInconsistencies { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 30;
    public int TotalFilteredItems { get; set; }
    public IReadOnlyList<ImportedInventoryItem> Items { get; set; } = [];
    public IReadOnlyList<SyncedBuilding> Buildings { get; set; } = [];
    public IReadOnlyList<string> Categories { get; set; } = [];
    public IReadOnlyList<string> Statuses { get; set; } = [];
    public IReadOnlyList<FilterOptionViewModel> AvailableInconsistencyTypes { get; set; } = [];
    public IReadOnlyDictionary<int, string> InconsistencySummaries { get; set; } = new Dictionary<int, string>();
    public int TotalItems { get; set; }
    public int AssignedItems { get; set; }
    public int PendingItems { get; set; }
    public int SuggestedItems { get; set; }
    public int InconsistentItems { get; set; }
}

public class InventoryInconsistencyDetailViewModel
{
    public ImportedInventoryItem Item { get; set; } = null!;
    public string Summary { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = "/admin/inventory";
    public IReadOnlyList<InventoryInconsistencyReasonViewModel> Reasons { get; set; } = [];
    public IReadOnlyList<InventoryInconsistencyActionViewModel> SuggestedActions { get; set; } = [];
    public IReadOnlyList<InventoryInconsistencyRelatedItemViewModel> MergeCandidates { get; set; } = [];
    public int RecommendedMergeCount { get; set; }
}

public class InventoryInconsistencyReasonViewModel
{
    public string Title { get; set; } = string.Empty;
    public string SuggestedAction { get; set; } = string.Empty;
    public IReadOnlyList<InventoryInconsistencyRelatedItemViewModel> RelatedItems { get; set; } = [];
}

public class InventoryInconsistencyRelatedItemViewModel
{
    public int Id { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string ItemNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string UnitOrDepartment { get; set; } = string.Empty;
    public string OrganizationalUnit { get; set; } = string.Empty;
    public string ResponsibleUser { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string AssignmentLabel { get; set; } = string.Empty;
    public IReadOnlyList<string> MatchingFields { get; set; } = [];
    public int MatchingFieldCount { get; set; }
    public bool IsMergeRecommended { get; set; }
}

public class InventoryInconsistencyActionViewModel
{
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string IconClass { get; set; } = "bi bi-search";
    public string ButtonClass { get; set; } = "btn btn-outline-primary";
}

public class FilterOptionViewModel
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public class EditInventoryItemViewModel
{
    public ImportedInventoryItem Item { get; set; } = null!;
    public IReadOnlyList<SyncedBuilding> Buildings { get; set; } = [];
    public IReadOnlyList<SyncedRoom> Rooms { get; set; } = [];
    public IReadOnlyList<string> Categories { get; set; } = [];
    public IReadOnlyList<string> Statuses { get; set; } = [];
}

public class InventoryItemFormModel
{
    public string? ItemNumber { get; set; }
    public string? SerialNumber { get; set; }
    public string? Description { get; set; }
    public string? Lot { get; set; }
    public string? ResponsibleUser { get; set; }
    public string? Email { get; set; }
    public string? UnitOrDepartment { get; set; }
    public string? OrganizationalUnit { get; set; }
    public string? JobTitle { get; set; }
    public string? Installer { get; set; }
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public string? AnnexPhone { get; set; }
    public string? Observation { get; set; }
    public string? TicketMda { get; set; }
    public string? InferredCategory { get; set; }
    public string? InferredStatus { get; set; }
    public string? AssignedBuildingExternalId { get; set; }
    public string? AssignedRoomExternalId { get; set; }
    public int? AssignedFloor { get; set; }
    public string? AssignmentNotes { get; set; }
}

public class CreateInventoryItemViewModel
{
    public InventoryItemFormModel Form { get; set; } = new();
    public IReadOnlyList<SyncedBuilding> Buildings { get; set; } = [];
    public IReadOnlyList<SyncedRoom> Rooms { get; set; } = [];
    public IReadOnlyList<string> Categories { get; set; } = [];
    public IReadOnlyList<string> Statuses { get; set; } = [];
}

public class AdminLocationsViewModel
{
    public string Search { get; set; } = string.Empty;
    public string Campus { get; set; } = string.Empty;
    public string Floor { get; set; } = string.Empty;
    public string SortBy { get; set; } = "building";
    public string SortDirection { get; set; } = "asc";
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
