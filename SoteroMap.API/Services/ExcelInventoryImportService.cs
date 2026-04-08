using System.Globalization;
using System.IO.Compression;
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

    public async Task<ExcelImportResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        var excelPath = ResolveExcelPath();
        if (!File.Exists(excelPath))
        {
            throw new FileNotFoundException("No se encontró el Excel de inventario en la ruta configurada.", excelPath);
        }

        var rows = ParseWorksheet(excelPath);
        var importedAt = DateTime.UtcNow;
        var sourceFile = Path.GetFileName(excelPath);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        _context.ImportedInventoryItems.RemoveRange(await _context.ImportedInventoryItems.ToListAsync(cancellationToken));
        await _context.SaveChangesAsync(cancellationToken);

        var items = rows.Select(row => new ImportedInventoryItem
        {
            RowNumber = row.RowNumber,
            ItemNumber = row.Get("NÚM"),
            SerialNumber = row.Get("PL_LOTE"),
            Description = row.Get("ITE_DESCRIPCION"),
            Lot = row.Get("PL_LOTE"),
            InstallDate = row.Get("FECHA INSTALACIÓN"),
            UnitOrDepartment = row.Get("UNIDAD O DEPTO"),
            OrganizationalUnit = row.Get("UNIDAD ORGANIZATIVA"),
            ResponsibleUser = row.Get("USUARIO RESPONSABLE"),
            Run = row.Get("RUN"),
            Email = row.Get("E-MAIL"),
            JobTitle = row.Get("CARGO"),
            IpAddress = FirstNonEmpty(row.Get("IP"), row.Get("IP_2")),
            AnnexPhone = row.Get("ANEXO/TELÉFONO"),
            ReplacedEquipment = row.Get("EQUIPO REEMPLAZADO"),
            TicketMda = row.Get("TICKET MDA"),
            Installer = row.Get("TÉCNICO INSTALADOR"),
            Observation = row.Get("OBSERVACIÓN"),
            Rut = row.Get("RUT"),
            InventoryDate = row.Get("FECHA INVENTARIO"),
            InferredCategory = InferCategory(row.Get("ITE_DESCRIPCION")),
            InferredStatus = InferStatus(row.Get("OBSERVACIÓN")),
            MatchConfidence = string.Empty,
            MatchNotes = string.Empty,
            SourceFile = sourceFile,
            ImportedAtUtc = importedAt
        }).ToList();

        _context.ImportedInventoryItems.AddRange(items);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ExcelImportResult
        {
            ExcelPath = excelPath,
            ImportedItemsCount = items.Count,
            ImportedAtUtc = importedAt,
            CategorySummary = items
                .GroupBy(i => i.InferredCategory)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase)
        };
    }

    private string ResolveExcelPath()
    {
        var configured = _configuration["ExcelImportPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return @"C:\Users\paolo.vilches\Downloads\Inventario tns 2026.xlsx";
    }

    private static List<ParsedRow> ParseWorksheet(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var sharedStrings = LoadSharedStrings(zip);
        var sheetDocument = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var rows = sheetDocument.Root!
            .Element(SpreadsheetNs + "sheetData")!
            .Elements(SpreadsheetNs + "row")
            .ToList();

        var headerRow = rows.First(r => GetRowNumber(r) == 4);
        var headersByColumn = BuildHeadersMap(headerRow, sharedStrings);

        return rows
            .Where(r => GetRowNumber(r) > 4)
            .Select(r => ParseRow(r, headersByColumn, sharedStrings))
            .Where(r => !string.IsNullOrWhiteSpace(r.Get("ITE_DESCRIPCION")) || !string.IsNullOrWhiteSpace(r.Get("PL_LOTE")))
            .ToList();
    }

    private static Dictionary<string, string> BuildHeadersMap(XElement headerRow, IReadOnlyList<string> sharedStrings)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in headerRow.Elements(SpreadsheetNs + "c"))
        {
            var reference = (string?)cell.Attribute("r") ?? string.Empty;
            var column = GetColumnName(reference);
            var value = GetCellValue(cell, sharedStrings);

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

        if ((header.Equals("FECHA INSTALACIÓN", StringComparison.OrdinalIgnoreCase) ||
             header.Equals("FECHA INVENTARIO", StringComparison.OrdinalIgnoreCase)) &&
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
        var text = description.ToUpperInvariant();

        if (text.Contains("NOTEBOOK") || text.Contains("DESKTOP") || text.Contains("THINKPAD") || text.Contains("AIO"))
            return "pc";
        if (text.Contains("IMPRESORA") || text.Contains("PRINTER"))
            return "printer";
        if (text.Contains("SCAN") || text.Contains("ESCANER") || text.Contains("SCANNER"))
            return "scanner";

        return "other";
    }

    private static string InferStatus(string observation)
    {
        var text = observation.ToUpperInvariant();

        if (text.Contains("ROBADO"))
            return "stolen";
        if (text.Contains("BRECHA"))
            return "gap";
        if (text.Contains("BAJA"))
            return "retired";

        return "active";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    }

    private sealed record ParsedRow(int RowNumber, Dictionary<string, string> Values)
    {
        public string Get(string key) => Values.TryGetValue(key, out var value) ? value : string.Empty;
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
        public DateTime ImportedAtUtc { get; set; }
        public Dictionary<string, int> CategorySummary { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
