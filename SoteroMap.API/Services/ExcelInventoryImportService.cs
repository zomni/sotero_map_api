using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;

namespace SoteroMap.API.Services;

public class ExcelInventoryImportService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;

    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public ExcelInventoryImportService(AppDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    public async Task<ExcelImportStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var count = await _context.ImportedInventoryItems.CountAsync(cancellationToken);
        var lastImport = await _context.ImportedInventoryItems
            .OrderByDescending(i => i.ImportedAtUtc)
            .Select(i => (DateTime?)i.ImportedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return new ExcelImportStatus
        {
            ExcelPath = ResolveExcelPath(),
            ImportedItemsCount = count,
            LastImportUtc = lastImport
        };
    }

    public async Task<ExcelImportResult> ImportAsync(
        string? fileName = null,
        string? sheetName = null,
        bool merge = false,
        CancellationToken cancellationToken = default)
    {
        var excelPath = ResolveExcelPath(fileName);
        if (!File.Exists(excelPath))
        {
            throw new FileNotFoundException("No se encontro el Excel de inventario en la ruta configurada.", excelPath);
        }

        var rows = ParseWorksheet(excelPath, sheetName);
        var importedAt = DateTime.UtcNow;
        var baseSourceFile = Path.GetFileName(excelPath);
        var sourceFile = string.IsNullOrWhiteSpace(sheetName)
            ? baseSourceFile
            : $"{baseSourceFile} :: {sheetName}";
        var buildings = await _context.SyncedBuildings.AsNoTracking().ToListAsync(cancellationToken);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        if (!merge)
        {
            var existingItemsFromSameSource = await _context.ImportedInventoryItems
                .Where(i => i.SourceFile == sourceFile)
                .ToListAsync(cancellationToken);

            if (existingItemsFromSameSource.Count > 0)
            {
                _context.ImportedInventoryItems.RemoveRange(existingItemsFromSameSource);
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        var existingInventoryItems = merge
            ? await _context.ImportedInventoryItems.ToListAsync(cancellationToken)
            : [];

        var insertedItems = new List<ImportedInventoryItem>();
        var mergedItemsCount = 0;

        foreach (var row in rows)
        {
            var physicalLocation = row.Get("UBICACION FISICA");
            var matchedBuilding = ResolveBuildingByPhysicalLocation(physicalLocation, buildings);
            var candidate = new ImportedInventoryItem
            {
                RowNumber = row.RowNumber,
                ItemNumber = FirstNonEmpty(row.Get("NUM"), row.Get("N")),
                SerialNumber = FirstNonEmpty(row.Get("PL_LOTE"), row.Get("S_N")),
                Description = FirstNonEmpty(row.Get("ITE_DESCRIPCION"), "Equipo"),
                Lot = row.Get("PL_LOTE"),
                InstallDate = FirstNonEmpty(row.Get("FECHA INSTALACION"), row.Get("FECHA INSTALACION_2")),
                UnitOrDepartment = row.Get("UNIDAD O DEPTO"),
                OrganizationalUnit = row.Get("UNIDAD ORGANIZATIVA"),
                ResponsibleUser = row.Get("USUARIO RESPONSABLE"),
                Run = FirstNonEmpty(row.Get("RUN"), row.Get("RUT")),
                Email = row.Get("E-MAIL"),
                JobTitle = row.Get("CARGO"),
                IpAddress = FirstNonEmpty(row.Get("IP"), row.Get("IP_2")),
                MacAddress = row.Get("MAC"),
                AnnexPhone = row.Get("ANEXO/TELEFONO"),
                ReplacedEquipment = row.Get("EQUIPO REEMPLAZADO"),
                TicketMda = row.Get("TICKET MDA"),
                Installer = row.Get("TECNICO INSTALADOR"),
                Observation = row.Get("OBSERVACION"),
                Rut = row.Get("RUT"),
                InventoryDate = row.Get("FECHA INVENTARIO"),
                InferredCategory = InferCategory(row.Get("ITE_DESCRIPCION")),
                InferredStatus = InferStatus(row.Get("OBSERVACION")),
                MatchedBuildingExternalId = matchedBuilding?.ExternalId ?? string.Empty,
                MatchConfidence = matchedBuilding is null ? string.Empty : "import-physical-location",
                MatchNotes = matchedBuilding is null
                    ? string.Empty
                    : $"Autoasignado por UBICACION FISICA: {physicalLocation}",
                AssignedBuildingExternalId = matchedBuilding?.ExternalId ?? string.Empty,
                AssignedRoomExternalId = string.Empty,
                AssignedFloor = null,
                AssignmentNotes = matchedBuilding is null
                    ? string.Empty
                    : $"Asignado automaticamente por UBICACION FISICA: {physicalLocation}",
                AssignmentUpdatedAtUtc = matchedBuilding is null ? null : importedAt,
                SourceFile = sourceFile,
                ImportedAtUtc = importedAt
            };

            if (merge && TryFindExistingItem(existingInventoryItems, candidate, out var existingItem))
            {
                MergeIntoExistingItem(existingItem!, candidate);
                mergedItemsCount++;
                continue;
            }

            insertedItems.Add(candidate);
            if (merge)
            {
                existingInventoryItems.Add(candidate);
            }
        }

        _context.ImportedInventoryItems.AddRange(insertedItems);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ExcelImportResult
        {
            ExcelPath = excelPath,
            ImportedItemsCount = insertedItems.Count,
            MergedItemsCount = mergedItemsCount,
            ImportedAtUtc = importedAt,
            CategorySummary = insertedItems
                .GroupBy(i => i.InferredCategory)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase)
        };
    }

    private string ResolveExcelPath(string? fileName = null)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var root = _configuration["ExcelImportRoot"];
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("ExcelImportRoot no esta configurado para resolver archivos por nombre.");
            }

            return Path.Combine(root, Path.GetFileName(fileName));
        }

        var configured = _configuration["ExcelImportPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return string.Empty;
    }

    private static List<ParsedRow> ParseWorksheet(string path, string? sheetName = null)
    {
        using var zip = ZipFile.OpenRead(path);

        var sharedStrings = LoadSharedStrings(zip);
        var sheetDocument = LoadWorksheet(zip, sheetName);
        var rows = sheetDocument.Root!
            .Element(SpreadsheetNs + "sheetData")!
            .Elements(SpreadsheetNs + "row")
            .ToList();

        var headerRow = rows.FirstOrDefault(r => IsHeaderRow(r, sharedStrings))
            ?? throw new InvalidOperationException("No se encontraron encabezados reconocibles en la primera hoja del Excel.");

        var headerRowNumber = GetRowNumber(headerRow);
        var headersByColumn = BuildHeadersMap(headerRow, sharedStrings);

        return rows
            .Where(r => GetRowNumber(r) > headerRowNumber)
            .Select(r => ParseRow(r, headersByColumn, sharedStrings))
            .Where(r =>
                !string.IsNullOrWhiteSpace(r.Get("ITE_DESCRIPCION")) ||
                !string.IsNullOrWhiteSpace(r.Get("PL_LOTE")) ||
                !string.IsNullOrWhiteSpace(r.Get("S_N")))
            .ToList();
    }

    private static XDocument LoadWorksheet(ZipArchive zip, string? sheetName)
    {
        var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
        var workbookRels = XDocument.Load(zip.GetEntry("xl/_rels/workbook.xml.rels")!.Open());

        var sheets = workbook.Root!
            .Element(SpreadsheetNs + "sheets")!
            .Elements(SpreadsheetNs + "sheet");

        var selectedSheet = string.IsNullOrWhiteSpace(sheetName)
            ? sheets.First()
            : sheets.FirstOrDefault(sheet => string.Equals((string?)sheet.Attribute("name"), sheetName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No se encontro la hoja '{sheetName}' en el Excel.");

        var relationshipId = (string?)selectedSheet.Attribute(OfficeRelNs + "id")
            ?? throw new InvalidOperationException("No se pudo resolver la hoja del Excel.");

        var target = workbookRels.Root!
            .Elements(PackageRelNs + "Relationship")
            .First(r => string.Equals((string?)r.Attribute("Id"), relationshipId, StringComparison.Ordinal))
            .Attribute("Target")?
            .Value
            ?? throw new InvalidOperationException("No se encontro el archivo de la hoja del Excel.");

        if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
        {
            target = $"xl/{target}";
        }

        var sheetEntry = zip.GetEntry(target)
            ?? throw new InvalidOperationException("No se pudo abrir la hoja del Excel.");

        return XDocument.Load(sheetEntry.Open());
    }

    private static bool IsHeaderRow(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var values = row.Elements(SpreadsheetNs + "c")
            .Select(cell => NormalizeHeader(GetCellValue(cell, sharedStrings)))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (values.Contains("ITE_DESCRIPCION") && values.Contains("PL_LOTE")) ||
               (values.Contains("S_N") && values.Contains("UNIDAD_O_DEPTO"));
    }

    private static Dictionary<string, string> BuildHeadersMap(XElement headerRow, IReadOnlyList<string> sharedStrings)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in headerRow.Elements(SpreadsheetNs + "c"))
        {
            var reference = (string?)cell.Attribute("r") ?? string.Empty;
            var column = GetColumnName(reference);
            var value = NormalizeHeader(GetCellValue(cell, sharedStrings));

            if (string.IsNullOrWhiteSpace(column) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!duplicates.TryAdd(value, 1))
            {
                duplicates[value]++;
                value = $"{value}_{duplicates[value]}";
            }

            headers[column] = value;
        }

        return headers;
    }

    private static ParsedRow ParseRow(
        XElement row,
        IReadOnlyDictionary<string, string> headersByColumn,
        IReadOnlyList<string> sharedStrings)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rowNumber = GetRowNumber(row);

        foreach (var cell in row.Elements(SpreadsheetNs + "c"))
        {
            var reference = (string?)cell.Attribute("r") ?? string.Empty;
            var column = GetColumnName(reference);
            if (string.IsNullOrWhiteSpace(column) || !headersByColumn.TryGetValue(column, out var header))
            {
                continue;
            }

            values[header] = NormalizeValue(header, GetCellValue(cell, sharedStrings));
        }

        return new ParsedRow(rowNumber, values);
    }

    private static List<string> LoadSharedStrings(ZipArchive zip)
    {
        var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (sharedEntry is null)
        {
            return [];
        }

        var sharedDocument = XDocument.Load(sharedEntry.Open());
        return sharedDocument.Root!
            .Elements(SpreadsheetNs + "si")
            .Select(si => string.Concat(si.Descendants(SpreadsheetNs + "t").Select(t => (string?)t ?? string.Empty)))
            .ToList();
    }

    private static string GetCellValue(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");
        var raw = (string?)cell.Element(SpreadsheetNs + "v") ?? string.Empty;

        if (type == "s" && int.TryParse(raw, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedIndex];
        }

        return raw;
    }

    private static string NormalizeValue(string header, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if ((header.Equals("FECHA_INSTALACION", StringComparison.OrdinalIgnoreCase) ||
             header.Equals("FECHA_INVENTARIO", StringComparison.OrdinalIgnoreCase)) &&
            double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial))
        {
            return DateTime.FromOADate(serial).ToString("yyyy-MM-dd");
        }

        return value.Trim();
    }

    private static int GetRowNumber(XElement row)
    {
        return int.TryParse((string?)row.Attribute("r"), out var number) ? number : 0;
    }

    private static string GetColumnName(string cellReference)
    {
        return new string(cellReference.TakeWhile(char.IsLetter).ToArray());
    }

    private static string InferCategory(string description)
    {
        var text = NormalizeText(description);

        if (text.Contains("NOTEBOOK", StringComparison.Ordinal) ||
            text.Contains("DESKTOP", StringComparison.Ordinal) ||
            text.Contains("THINKPAD", StringComparison.Ordinal) ||
            text.Contains("AIO", StringComparison.Ordinal) ||
            text.Contains("OPTIPLEX", StringComparison.Ordinal))
            return "pc";

        if (text.Contains("IMPRESORA", StringComparison.Ordinal) || text.Contains("PRINTER", StringComparison.Ordinal))
            return "printer";
        if (text.Contains("ESCANER", StringComparison.Ordinal) || text.Contains("SCANNER", StringComparison.Ordinal) || text.Contains("SCAN ", StringComparison.Ordinal))
            return "scanner";

        return "other";
    }

    private static string InferStatus(string observation)
    {
        var text = NormalizeText(observation);

        if (text.Contains("ROBADO", StringComparison.Ordinal))
            return "stolen";
        if (text.Contains("BRECHA", StringComparison.Ordinal))
            return "gap";
        if (text.Contains("BAJA", StringComparison.Ordinal))
            return "retired";

        return "active";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    }

    private static string NormalizeHeader(string value)
    {
        var normalized = NormalizeText(value);
        return normalized.Replace(' ', '_');
    }

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(character);
        }

        var cleaned = builder
            .ToString()
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();

        cleaned = Regex.Replace(cleaned, "[^A-Z0-9]+", " ");
        return Regex.Replace(cleaned, "\\s+", " ").Trim();
    }

    private static IEnumerable<string> BuildAliases(SyncedBuilding building)
    {
        var rawAliases = new[]
        {
            building.EffectiveDisplayName,
            building.RealName,
            building.ShortName
        };

        foreach (var alias in rawAliases)
        {
            var normalized = NormalizeLocationForMatching(alias);
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized;
        }
    }

    private static SyncedBuilding? ResolveBuildingByPhysicalLocation(
        string physicalLocation,
        IEnumerable<SyncedBuilding> buildings)
    {
        var normalizedLocation = NormalizeLocationForMatching(physicalLocation);
        if (string.IsNullOrWhiteSpace(normalizedLocation))
            return null;

        var matches = buildings
            .Select(building => new
            {
                Building = building,
                Aliases = BuildAliases(building).ToList()
            })
            .Where(entry => entry.Aliases.Any(alias => ContainsPhrase(normalizedLocation, alias)))
            .Select(entry => entry.Building)
            .ToList();

        if (matches.Count != 1)
            return null;

        return matches[0];
    }

    private static string NormalizeLocationForMatching(string value)
    {
        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        normalized = Regex.Replace(normalized, "\\bHSR\\b", " ");
        normalized = Regex.Replace(normalized, "\\bCASR\\b", " ");
        normalized = Regex.Replace(normalized, "\\bSSMSO\\b", " ");
        normalized = Regex.Replace(normalized, "\\bHOSPITAL\\b", " ");
        normalized = Regex.Replace(normalized, "\\bBLOQUE\\b", "BLOCK");
        normalized = Regex.Replace(normalized, "\\bBLOCK\\s+CENTRAL\\s+\\d+\\s+PISO\\b", "BLOCK CENTRAL");
        normalized = Regex.Replace(normalized, "\\b\\d+\\s+PISO\\b", " ");
        normalized = Regex.Replace(normalized, "\\bPISO\\s+\\d+\\b", " ");
        normalized = Regex.Replace(normalized, "\\b\\d+O\\s+PISO\\b", " ");
        normalized = Regex.Replace(normalized, "\\b\\d+\\b", " ");
        normalized = Regex.Replace(normalized, "\\s+", " ").Trim();

        return normalized;
    }

    private static bool ContainsPhrase(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle))
            return false;

        return haystack.Equals(needle, StringComparison.Ordinal) ||
               haystack.Contains($" {needle} ", StringComparison.Ordinal) ||
               haystack.StartsWith($"{needle} ", StringComparison.Ordinal) ||
               haystack.EndsWith($" {needle}", StringComparison.Ordinal);
    }

    private sealed record ParsedRow(int RowNumber, Dictionary<string, string> Values)
    {
        public string Get(string key)
        {
            var normalizedKey = NormalizeHeader(key);
            return Values.TryGetValue(normalizedKey, out var value) ? value : string.Empty;
        }
    }

    public sealed class ExcelImportStatus
    {
        public string ExcelPath { get; set; } = string.Empty;
        public int ImportedItemsCount { get; set; }
        public DateTime? LastImportUtc { get; set; }
    }

    public sealed class ExcelImportResult
    {
        public string ExcelPath { get; set; } = string.Empty;
        public int ImportedItemsCount { get; set; }
        public int MergedItemsCount { get; set; }
        public DateTime ImportedAtUtc { get; set; }
        public Dictionary<string, int> CategorySummary { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryFindExistingItem(
        IEnumerable<ImportedInventoryItem> existingItems,
        ImportedInventoryItem candidate,
        out ImportedInventoryItem? existingItem)
    {
        existingItem = null;

        if (!string.IsNullOrWhiteSpace(candidate.SerialNumber))
        {
            existingItem = existingItems.FirstOrDefault(item =>
                string.Equals(item.SerialNumber, candidate.SerialNumber, StringComparison.OrdinalIgnoreCase));
        }

        if (existingItem is not null)
            return true;

        if (!string.IsNullOrWhiteSpace(candidate.MacAddress))
        {
            existingItem = existingItems.FirstOrDefault(item =>
                string.Equals(item.MacAddress, candidate.MacAddress, StringComparison.OrdinalIgnoreCase));
        }

        return existingItem is not null;
    }

    private static void MergeIntoExistingItem(ImportedInventoryItem existingItem, ImportedInventoryItem candidate)
    {
        existingItem.ItemNumber = Prefer(existingItem.ItemNumber, candidate.ItemNumber);
        existingItem.SerialNumber = Prefer(existingItem.SerialNumber, candidate.SerialNumber);
        existingItem.Description = Prefer(existingItem.Description, candidate.Description);
        existingItem.Lot = Prefer(existingItem.Lot, candidate.Lot);
        existingItem.InstallDate = Prefer(existingItem.InstallDate, candidate.InstallDate);
        existingItem.UnitOrDepartment = Prefer(existingItem.UnitOrDepartment, candidate.UnitOrDepartment);
        existingItem.OrganizationalUnit = Prefer(existingItem.OrganizationalUnit, candidate.OrganizationalUnit);
        existingItem.ResponsibleUser = Prefer(existingItem.ResponsibleUser, candidate.ResponsibleUser);
        existingItem.Run = Prefer(existingItem.Run, candidate.Run);
        existingItem.Email = Prefer(existingItem.Email, candidate.Email);
        existingItem.JobTitle = Prefer(existingItem.JobTitle, candidate.JobTitle);
        existingItem.IpAddress = Prefer(existingItem.IpAddress, candidate.IpAddress);
        existingItem.MacAddress = Prefer(existingItem.MacAddress, candidate.MacAddress);
        existingItem.AnnexPhone = Prefer(existingItem.AnnexPhone, candidate.AnnexPhone);
        existingItem.ReplacedEquipment = Prefer(existingItem.ReplacedEquipment, candidate.ReplacedEquipment);
        existingItem.TicketMda = Prefer(existingItem.TicketMda, candidate.TicketMda);
        existingItem.Installer = Prefer(existingItem.Installer, candidate.Installer);
        existingItem.Observation = Prefer(existingItem.Observation, candidate.Observation);
        existingItem.Rut = Prefer(existingItem.Rut, candidate.Rut);
        existingItem.InventoryDate = Prefer(existingItem.InventoryDate, candidate.InventoryDate);
        existingItem.InferredCategory = Prefer(existingItem.InferredCategory, candidate.InferredCategory);
        existingItem.InferredStatus = Prefer(existingItem.InferredStatus, candidate.InferredStatus);
        existingItem.MatchConfidence = Prefer(existingItem.MatchConfidence, candidate.MatchConfidence);
        existingItem.MatchNotes = Prefer(existingItem.MatchNotes, candidate.MatchNotes);

        if (string.IsNullOrWhiteSpace(existingItem.AssignedBuildingExternalId) && !string.IsNullOrWhiteSpace(candidate.AssignedBuildingExternalId))
        {
            existingItem.AssignedBuildingExternalId = candidate.AssignedBuildingExternalId;
            existingItem.AssignmentNotes = Prefer(existingItem.AssignmentNotes, candidate.AssignmentNotes);
            existingItem.AssignmentUpdatedAtUtc = candidate.AssignmentUpdatedAtUtc;
        }

        existingItem.ImportedAtUtc = candidate.ImportedAtUtc;
    }

    private static string Prefer(string currentValue, string candidateValue)
    {
        if (string.IsNullOrWhiteSpace(candidateValue))
            return currentValue;

        if (string.IsNullOrWhiteSpace(currentValue) || currentValue == "0")
            return candidateValue;

        return currentValue;
    }
}

