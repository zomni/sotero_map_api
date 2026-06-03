using System.Text.Json;

namespace SoteroMap.API.Services;

public static class BuildingFloorNormalizer
{
    public static string NormalizeCsv(string? floorsCsv)
    {
        var rawValue = floorsCsv?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawValue))
            return string.Empty;

        var floors = rawValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => int.TryParse(value, out _))
            .Select(int.Parse)
            .ToList();

        return NormalizeFloors(floors);
    }

    public static string NormalizeJson(string? floorsJson)
    {
        if (string.IsNullOrWhiteSpace(floorsJson))
            return string.Empty;

        try
        {
            var floors = JsonSerializer.Deserialize<List<int>>(floorsJson) ?? new List<int>();
            return NormalizeFloors(floors);
        }
        catch
        {
            return floorsJson.Trim();
        }
    }

    public static string NormalizeFloors(IEnumerable<int> floors)
    {
        var normalized = floors
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        if (normalized.Contains(0) && !normalized.Contains(1))
        {
            normalized.Add(1);
            normalized.Sort();
        }

        return normalized.Count == 0 ? string.Empty : JsonSerializer.Serialize(normalized);
    }
}
