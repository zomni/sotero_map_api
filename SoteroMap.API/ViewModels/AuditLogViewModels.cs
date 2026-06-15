namespace SoteroMap.API.ViewModels;

public class AuditLogQueryRequest
{
    public string BuildingExternalId { get; set; } = string.Empty;
    public string ChangedByUsername { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Search { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string DateFrom { get; set; } = string.Empty;
    public string DateTo { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public class AuditLogQueryResultViewModel
{
    public IReadOnlyList<ActivityLogListItemViewModel> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalPages { get; set; } = 1;
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int CriticalCount { get; set; }
    public int WarningCount { get; set; }
}
