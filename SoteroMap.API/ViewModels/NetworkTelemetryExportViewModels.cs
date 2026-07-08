namespace SoteroMap.API.ViewModels;

public class NetworkTelemetryExportRepeatedUserViewModel
{
    public string Username { get; set; } = string.Empty;
    public int Apariciones { get; set; }
    public int HostsDistintos { get; set; }
    public int IPsDistintas { get; set; }
    public int RiesgoMax { get; set; }
    public string NivelRiesgo { get; set; } = string.Empty;
    public string TipoSospecha { get; set; } = string.Empty;
}

public class NetworkTelemetryExportRiskCauseViewModel
{
    public string Causa { get; set; } = string.Empty;
    public int Cantidad { get; set; }
    public double Porcentaje { get; set; }
}

public class NetworkTelemetryExportDataViewModel
{
    public int SnapshotId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public int RiskScore { get; set; }
    public int DeviceCount { get; set; }
    public int HighRiskDeviceCount { get; set; }
    public int MediumRiskDeviceCount { get; set; }
    public int LowRiskDeviceCount { get; set; }
    public int ConnectedUserCount { get; set; }
    public DateTime? ObservedAtUtc { get; set; }
    public DateTime? WindowStartUtc { get; set; }
    public DateTime? WindowEndUtc { get; set; }
    public IReadOnlyList<NetworkTelemetryObservationViewModel> Devices { get; set; } = [];
    public IReadOnlyList<NetworkTelemetryExportRepeatedUserViewModel> RepeatedUsers { get; set; } = [];
    public IReadOnlyList<NetworkTelemetryExportRiskCauseViewModel> RiskCauses { get; set; } = [];
}
