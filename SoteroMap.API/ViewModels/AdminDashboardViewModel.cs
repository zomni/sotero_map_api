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
    public string DatabaseFileName { get; set; } = string.Empty;
    public long DatabaseFileSizeBytes { get; set; }
    public DateTime? DatabaseLastWriteUtc { get; set; }
    public string FrontendMapUrl { get; set; } = string.Empty;
    public IReadOnlyList<DatabaseBackupViewModel> DatabaseBackups { get; set; } = [];
    public IReadOnlyList<DashboardCategorySummaryViewModel> CategoryBreakdown { get; set; } = [];
    public IReadOnlyList<DashboardInventoryPreviewViewModel> RecentItems { get; set; } = [];
    public IReadOnlyList<ActivityLogListItemViewModel> RecentActivity { get; set; } = [];
}

public class DatabaseBackupViewModel
{
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
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
    public string Resource { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string PreviousValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
    public string ChangedByUsername { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public class ComplianceDashboardViewModel
{
    public string OverallStatus { get; set; } = "Advertencia";
    public string OverallStatusLabel { get; set; } = "Advertencia";
    public IReadOnlyList<ComplianceCheckViewModel> Checks { get; set; } = [];
    public IReadOnlyList<ComplianceSummaryCardViewModel> SummaryCards { get; set; } = [];
    public IReadOnlyList<ComplianceEventViewModel> RecentBackups { get; set; } = [];
    public IReadOnlyList<ComplianceEventViewModel> RecentAccesses { get; set; } = [];
    public IReadOnlyList<ComplianceEventViewModel> CriticalEvents { get; set; } = [];
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int AdminUsers { get; set; }
    public int AdminUsersWithMfa { get; set; }
    public int FailedLoginsLast7Days { get; set; }
    public int CriticalEventsLast7Days { get; set; }
    public int RecentBackupsCount { get; set; }
    public int RecentAccessEventsCount { get; set; }
    public bool DatabaseConnected { get; set; }
    public bool HttpsConfigured { get; set; }
    public bool HttpsActive { get; set; }
    public bool SwaggerRestricted { get; set; }
    public bool MfaCompliant { get; set; }
    public bool BackupEnabled { get; set; }
    public bool BackupHealthy { get; set; }
    public bool LdapsConfigured { get; set; }
    public bool DataIntegrityHealthy { get; set; }
    public string DatabaseFileName { get; set; } = string.Empty;
    public long DatabaseFileSizeBytes { get; set; }
    public DateTime? DatabaseLastWriteUtc { get; set; }
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
}

public class ComplianceCheckViewModel
{
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Status { get; set; } = "warning";
    public string IconClass { get; set; } = "bi bi-question-circle";
}

public class ComplianceSummaryCardViewModel
{
    public string Title { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string IconClass { get; set; } = "bi bi-info-circle";
    public string Tone { get; set; } = "primary";
}

public class ComplianceEventViewModel
{
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Badge { get; set; } = string.Empty;
    public string BadgeClass { get; set; } = "bg-secondary";
    public string TimeLabel { get; set; } = string.Empty;
}
