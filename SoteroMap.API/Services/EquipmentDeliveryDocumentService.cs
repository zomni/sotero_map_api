using System.IO.Compression;
using System.Xml.Linq;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Services;

public class EquipmentDeliveryDocumentService
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private readonly IWebHostEnvironment _environment;

    public EquipmentDeliveryDocumentService(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public async Task<(byte[] Content, string FileName)> GenerateAsync(EquipmentDeliveryFormViewModel model)
    {
        var templatePath = Path.Combine(_environment.ContentRootPath, "Templates", "FormularioEntregaEquipo.docx");
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("No se encontro la plantilla del formulario.", templatePath);
        }

        await using var templateStream = File.OpenRead(templatePath);
        await using var outputStream = new MemoryStream();
        await templateStream.CopyToAsync(outputStream);
        outputStream.Position = 0;

        using (var archive = new ZipArchive(outputStream, ZipArchiveMode.Update, true))
        {
            var documentEntry = archive.GetEntry("word/document.xml")
                ?? throw new InvalidOperationException("La plantilla no contiene word/document.xml");

            XDocument document;
            using (var entryStream = documentEntry.Open())
            {
                document = XDocument.Load(entryStream, LoadOptions.PreserveWhitespace);
            }

            var tables = document.Descendants(W + "tbl").ToList();
            if (tables.Count < 4)
            {
                throw new InvalidOperationException("La plantilla no tiene la estructura esperada de tablas.");
            }

            FillHeaderTable(tables[0], model);
            FillComputerTable(tables[1], model);
            FillPeripheralTable(tables[2], model);
            FillApplicationsTable(tables[3], model);
            FillSignatureLine(document, model);

            documentEntry.Delete();
            var newEntry = archive.CreateEntry("word/document.xml", CompressionLevel.Optimal);
            await using var newStream = newEntry.Open();
            document.Save(newStream);
        }

        var fileName = BuildFileName(model);
        return (outputStream.ToArray(), fileName);
    }

    private static void FillHeaderTable(XElement table, EquipmentDeliveryFormViewModel model)
    {
        SetCellText(table, 0, 1, model.Institution);
        SetCellText(table, 0, 3, model.DocumentDate);
        SetCellText(table, 1, 1, model.UnitOrDepartment);
        SetCellText(table, 2, 1, model.SerialNumber);
        SetCellText(table, 3, 1, model.ResponsibleUser);
        SetCellText(table, 4, 1, model.Email);
        SetCellText(table, 4, 3, model.IpAddress);
        SetCellText(table, 5, 1, model.JobTitle);
        SetCellText(table, 5, 3, model.Annex);
        SetCellText(table, 6, 1, model.ActiveDirectoryUser);
        SetCellText(table, 7, 2, model.ReceptionType);
        SetCellText(table, 8, 2, model.ReplacedEquipmentSerial);
        SetCellText(table, 9, 2, model.ReplacedEquipmentModel);
        SetCellText(table, 10, 2, model.OfficeActivationEmail);
        SetCellText(table, 11, 2, model.MdaTicket);
        SetCellText(table, 12, 2, model.MacAddress);
    }

    private static void FillComputerTable(XElement table, EquipmentDeliveryFormViewModel model)
    {
        SetCellText(table, 1, 1, model.ComputerBrand);
        SetCellText(table, 1, 3, model.OperatingSystem);
        SetCellText(table, 2, 1, model.ComputerModel);
        SetCellText(table, 2, 3, model.OfficeSuite);
        SetCellText(table, 3, 1, model.Processor);
        SetCellText(table, 3, 3, model.SecurityLock);
        SetCellText(table, 4, 1, model.Ram);
        SetCellText(table, 5, 1, model.Disk);
    }

    private static void FillPeripheralTable(XElement table, EquipmentDeliveryFormViewModel model)
    {
        SetCellText(table, 1, 1, ToMark(model.Lexmark));
        SetCellText(table, 2, 1, ToMark(model.Zebra));
        SetCellText(table, 3, 1, ToMark(model.FingerprintReader));
        SetCellText(table, 4, 1, ToMark(model.OtherDevices));
    }

    private static void FillApplicationsTable(XElement table, EquipmentDeliveryFormViewModel model)
    {
        SetCellText(table, 1, 1, ToMark(model.AppRcePulso));
        SetCellText(table, 1, 3, ToMark(model.AdminSgd));
        SetCellText(table, 1, 7, model.ValidationSerialName);

        SetCellText(table, 2, 1, ToMark(model.AppRce));
        SetCellText(table, 2, 3, ToMark(model.AdminSirh));
        SetCellText(table, 2, 7, model.ValidationDescriptionChange);

        SetCellText(table, 3, 1, ToMark(model.AppAnatPatologica));
        SetCellText(table, 3, 3, ToMark(model.AdminAbastecimiento1));
        SetCellText(table, 3, 7, model.ValidationOfficeSuite);

        SetCellText(table, 4, 1, ToMark(model.AppHospitalizados));
        SetCellText(table, 4, 3, ToMark(model.AdminAbastecimiento2));
        SetCellText(table, 4, 7, model.ValidationAdAccount);

        SetCellText(table, 5, 1, ToMark(model.AppDauAdulto));
        SetCellText(table, 5, 3, ToMark(model.AdminMsAccess));

        SetCellText(table, 6, 1, ToMark(model.AppDauMujer));
        SetCellText(table, 6, 3, ToMark(model.AdminTeams));
        SetCellText(table, 6, 7, model.AntivirusInstalledVersion);

        SetCellText(table, 7, 1, ToMark(model.AppDauInfantil));
        SetCellText(table, 7, 3, ToMark(model.AdminAbastSsmso));
        SetCellText(table, 7, 7, model.AntivirusConnectionState);

        SetCellText(table, 8, 1, ToMark(model.AppIq));
        SetCellText(table, 8, 3, ToMark(model.AdminToadModeler));

        SetCellText(table, 9, 1, ToMark(model.AppInterconsultas));
        SetCellText(table, 9, 3, ToMark(model.AdminZoom));

        SetCellText(table, 10, 1, ToMark(model.AppSga));
        SetCellText(table, 10, 3, ToMark(model.AdminBizagi));

        SetCellText(table, 11, 1, ToMark(model.AppSgde));
        SetCellText(table, 11, 3, ToMark(model.AdminMsProject));

        SetCellText(table, 12, 1, ToMark(model.AppRpecRni));
        SetCellText(table, 12, 3, ToMark(model.AdminPowerBi));

        SetCellText(table, 13, 1, ToMark(model.AppHistorialClinico));
        SetCellText(table, 13, 3, ToMark(model.AdminTableauReader));
    }

    private static void FillSignatureLine(XDocument document, EquipmentDeliveryFormViewModel model)
    {
        var bookmark = document
            .Descendants(W + "bookmarkStart")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute(W + "name"), "_Hlk185929370", StringComparison.Ordinal));

        var paragraph = bookmark?.Ancestors(W + "p").FirstOrDefault();
        if (paragraph == null)
        {
            return;
        }

        paragraph.Elements().Remove();
        paragraph.Add(
            new XElement(W + "r", new XElement(W + "t", Normalize(model.SignedUserName))),
            new XElement(W + "r", new XElement(W + "tab")),
            new XElement(W + "r", new XElement(W + "t", Normalize(model.SignedUserRut))),
            new XElement(W + "r", new XElement(W + "tab")),
            new XElement(W + "r", new XElement(W + "tab")),
            new XElement(W + "r", new XElement(W + "t", Normalize(model.TechnicianName))));
    }

    private static void SetCellText(XElement table, int rowIndex, int cellIndex, string? value)
    {
        var row = table.Elements(W + "tr").ElementAtOrDefault(rowIndex);
        var cell = row?.Elements(W + "tc").ElementAtOrDefault(cellIndex);
        if (cell == null)
        {
            return;
        }

        var properties = cell.Element(W + "tcPr");
        cell.RemoveNodes();
        if (properties != null)
        {
            cell.Add(properties);
        }

        cell.Add(
            new XElement(W + "p",
                new XElement(W + "r",
                    new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), Normalize(value)))));
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? " " : value.Trim();

    private static string ToMark(bool enabled) => enabled ? "X" : string.Empty;

    private static string BuildFileName(EquipmentDeliveryFormViewModel model)
    {
        var serial = SanitizeFilePart(model.SerialNumber, "sin-serie");
        var user = SanitizeFilePart(model.ResponsibleUser, "sin-usuario");
        return $"formulario-entrega-{serial}-{user}.docx";
    }

    private static string SanitizeFilePart(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalidChar, '-');
        }

        normalized = normalized.Replace(' ', '-');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}
