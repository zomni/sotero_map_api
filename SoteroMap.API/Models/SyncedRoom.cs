namespace SoteroMap.API.Models;

public class SyncedRoom
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public int SyncedBuildingId { get; set; }
    public SyncedBuilding SyncedBuilding { get; set; } = null!;
    public string BuildingExternalId { get; set; } = string.Empty;
    public int Floor { get; set; }
    public int? ManualFloor { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ManualName { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Sector { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public bool IsMapped { get; set; }
    public string GeometryJson { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int? Capacity { get; set; }
    public int DevicesCount { get; set; }
    public string ResponsibleArea { get; set; } = string.Empty;
    public string ResponsiblePerson { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime SyncedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<SyncedEquipment> Equipments { get; set; } = new List<SyncedEquipment>();

    public string EffectiveName => string.IsNullOrWhiteSpace(ManualName) ? Name : ManualName;
    public int EffectiveFloor => ManualFloor ?? Floor;
}
