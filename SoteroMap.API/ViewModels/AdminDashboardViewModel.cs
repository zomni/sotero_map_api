namespace SoteroMap.API.ViewModels;

public class AdminDashboardViewModel
{
    public int SyncedBuildings { get; set; }
    public int SyncedRooms { get; set; }
    public int TotalImportedItems { get; set; }
    public int AssignedItems { get; set; }
    public int PendingAssignmentItems { get; set; }
    public int SuggestedItems { get; set; }
    public int StolenItems { get; set; }
    public int DistinctImportedCategories { get; set; }
    public IReadOnlyList<DashboardCategorySummaryViewModel> CategoryBreakdown { get; set; } = [];
    public IReadOnlyList<DashboardInventoryPreviewViewModel> RecentItems { get; set; } = [];
    public IReadOnlyList<ActivityLogListItemViewModel> RecentActivity { get; set; } = [];
}

public class DashboardCategorySummaryViewModel
{
    public string Category { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class DashboardInventoryPreviewViewModel
{
    public int Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ResponsibleUser { get; set; } = string.Empty;
    public string UnitOrDepartment { get; set; } = string.Empty;
    public string AssignedBuildingExternalId { get; set; } = string.Empty;
    public string InferredStatus { get; set; } = string.Empty;
}

public class ActivityLogListItemViewModel
{
    public int Id { get; set; }
    public string BuildingExternalId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string ChangedByUsername { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
