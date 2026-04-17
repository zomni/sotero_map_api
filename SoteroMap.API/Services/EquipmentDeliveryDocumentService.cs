using System.Diagnostics;
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

        return (outputStream.ToArray(), BuildFileName(model));
    }

    public async Task<(byte[] Content, string FileName)> GeneratePdfAsync(EquipmentDeliveryFormViewModel model, CancellationToken cancellationToken = default)
    {
        var generatedWord = await GenerateAsync(model);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "soteromap-delivery-preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var docxPath = Path.Combine(tempDirectory, generatedWord.FileName);
        var pdfFileName = Path.GetFileNameWithoutExtension(generatedWord.FileName) + ".pdf";
        var pdfPath = Path.Combine(tempDirectory, pdfFileName);

        try
        {
            var pdfCompatibleWord = PreparePdfCompatibleWord(generatedWord.Content);

            await File.WriteAllBytesAsync(docxPath, pdfCompatibleWord, cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = "soffice",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--convert-to");
            startInfo.ArgumentList.Add("pdf");
            startInfo.ArgumentList.Add("--outdir");
            startInfo.ArgumentList.Add(tempDirectory);
            startInfo.ArgumentList.Add(docxPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("No se pudo iniciar LibreOffice para convertir el formulario a PDF.");

            await process.WaitForExitAsync(cancellationToken);
            var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);

            if (process.ExitCode != 0 || !File.Exists(pdfPath))
            {
                var details = string.Join(" ", new[] { standardOutput, standardError }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                    ? "No se pudo convertir el formulario a PDF."
                    : $"No se pudo convertir el formulario a PDF: {details}");
            }

            var pdfBytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken);
            return (pdfBytes, pdfFileName);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }
    }

    private static byte[] PreparePdfCompatibleWord(byte[] wordContent)
    {
        using var outputStream = new MemoryStream();
        outputStream.Write(wordContent, 0, wordContent.Length);
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

            ConvertTechnicalDataLabelForPdf(document);

            documentEntry.Delete();
            var newEntry = archive.CreateEntry("word/document.xml", CompressionLevel.Optimal);
            using var newStream = newEntry.Open();
            document.Save(newStream);
        }

        return outputStream.ToArray();
    }

    private static void ConvertTechnicalDataLabelForPdf(XDocument document)
    {
        var targetCell = document
            .Descendants(W + "tc")
            .FirstOrDefault(cell => string.Join(" ", cell.Descendants(W + "t").Select(t => t.Value))
                .Contains("DATOS T", StringComparison.OrdinalIgnoreCase));

        if (targetCell == null)
        {
            return;
        }

        var cellProperties = targetCell.Element(W + "tcPr");
        if (cellProperties == null)
        {
            cellProperties = new XElement(W + "tcPr");
            targetCell.AddFirst(cellProperties);
        }

        cellProperties.Elements(W + "textDirection").Remove();
        cellProperties.Add(new XElement(W + "textDirection", new XAttribute(W + "val", "btLr")));

        var preservedProperties = new XElement(cellProperties);
        targetCell.RemoveNodes();
        targetCell.Add(preservedProperties);
        targetCell.Add(CreatePdfVerticalLabelParagraph("DATOS TECNICOS"));
    }

    private static XElement CreatePdfVerticalLabelParagraph(string value)
    {
        return new XElement(W + "p",
            new XElement(W + "pPr",
                new XElement(W + "jc", new XAttribute(W + "val", "center")),
                new XElement(W + "spacing",
                    new XAttribute(W + "before", "0"),
                    new XAttribute(W + "after", "0"),
                    new XAttribute(W + "line", "240"),
                    new XAttribute(W + "lineRule", "auto")),
                new XElement(W + "rPr",
                    new XElement(W + "rFonts",
                        new XAttribute(W + "ascii", "Calibri"),
                        new XAttribute(W + "hAnsi", "Calibri")),
                    new XElement(W + "b"),
                    new XElement(W + "bCs"),
                    new XElement(W + "sz", new XAttribute(W + "val", "20")),
                    new XElement(W + "szCs", new XAttribute(W + "val", "20")))),
            CreateStyledTextRun(value, "Calibri", 20, true));
    }

    private static XElement CreateStyledTextRun(string? value, string fontName, int fontSize, bool bold = false)
    {
        var runProperties = new XElement(W + "rPr",
            new XElement(W + "rFonts",
                new XAttribute(W + "ascii", fontName),
                new XAttribute(W + "hAnsi", fontName)));

        if (bold)
        {
            runProperties.Add(new XElement(W + "b"));
            runProperties.Add(new XElement(W + "bCs"));
        }

        runProperties.Add(new XElement(W + "sz", new XAttribute(W + "val", fontSize)));
        runProperties.Add(new XElement(W + "szCs", new XAttribute(W + "val", fontSize)));

        return new XElement(W + "r",
            runProperties,
            new XElement(W + "t",
                new XAttribute(XNamespace.Xml + "space", "preserve"),
                Normalize(value)));
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
        SetCellText(table, 1, 4, ToMark(model.AdminSgd));
        SetCellText(table, 1, 7, EffectiveValue(model.ValidationSerialName, model.SerialNumber));

        SetCellText(table, 2, 1, ToMark(model.AppRce));
        SetCellText(table, 2, 4, ToMark(model.AdminSirh));
        SetCellText(table, 2, 7, EffectiveValue(model.ValidationDescriptionChange, model.UnitOrDepartment));

        SetCellText(table, 3, 1, ToMark(model.AppAnatPatologica));
        SetCellText(table, 3, 4, ToMark(model.AdminAbastecimiento1));
        SetCellText(table, 3, 7, model.ValidationOfficeSuite);

        SetCellText(table, 4, 1, ToMark(model.AppHospitalizados));
        SetCellText(table, 4, 4, ToMark(model.AdminAbastecimiento2));
        SetCellText(table, 4, 7, EffectiveValue(model.ValidationAdAccount, model.ActiveDirectoryUser));

        SetCellText(table, 5, 1, ToMark(model.AppDauAdulto));
        SetCellText(table, 5, 4, ToMark(model.AdminMsAccess));
        SetCellText(table, 5, 7, string.Empty);

        SetCellText(table, 6, 1, ToMark(model.AppDauMujer));
        SetCellText(table, 6, 4, ToMark(model.AdminTeams));
        SetCellText(table, 6, 7, string.Empty);

        SetCellText(table, 7, 1, ToMark(model.AppDauInfantil));
        SetCellText(table, 7, 4, ToMark(model.AdminAbastSsmso));
        SetCellText(table, 7, 7, model.AntivirusInstalledVersion);

        SetCellText(table, 8, 1, ToMark(model.AppIq));
        SetCellText(table, 8, 4, ToMark(model.AdminToadModeler));
        SetCellText(table, 8, 7, model.AntivirusConnectionState);

        SetCellText(table, 9, 1, ToMark(model.AppInterconsultas));
        SetCellText(table, 9, 4, ToMark(model.AdminZoom));

        SetCellText(table, 10, 1, ToMark(model.AppSga));
        SetCellText(table, 10, 4, ToMark(model.AdminBizagi));

        SetCellText(table, 11, 1, ToMark(model.AppSgde));
        SetCellText(table, 11, 4, ToMark(model.AdminMsProject));

        SetCellText(table, 12, 1, ToMark(model.AppRpecRni));
        SetCellText(table, 12, 4, ToMark(model.AdminPowerBi));
        SetCellText(table, 12, 7, string.Empty);

        SetCellText(table, 13, 1, ToMark(model.AppHistorialClinico));
        SetCellText(table, 13, 4, ToMark(model.AdminTableauReader));
        SetCellText(table, 13, 7, string.Empty);
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

        var preservedElements = paragraph.Elements()
            .Where(e => e.Name == W + "pPr" || e.Name == W + "bookmarkStart" || e.Name == W + "bookmarkEnd")
            .Select(e => new XElement(e))
            .ToList();

        paragraph.RemoveNodes();
        foreach (var element in preservedElements)
        {
            paragraph.Add(element);
        }

        paragraph.Add(
            CreateTextRun(string.Empty),
            new XElement(W + "r", new XElement(W + "tab")),
            CreateTextRun(string.Empty),
            new XElement(W + "r", new XElement(W + "tab")),
            new XElement(W + "r", new XElement(W + "tab")),
            CreateTextRun(string.Empty));
    }

    private static void SetCellText(XElement table, int rowIndex, int cellIndex, string? value)
    {
        var row = table.Elements(W + "tr").ElementAtOrDefault(rowIndex);
        var cell = row?.Elements(W + "tc").ElementAtOrDefault(cellIndex);
        if (cell == null)
        {
            return;
        }

        SetCellText(cell, value);
    }

    private static void SetCellText(XElement cell, string? value)
    {
        var paragraph = cell.Elements(W + "p").FirstOrDefault();
        if (paragraph == null)
        {
            paragraph = new XElement(W + "p");
            cell.Add(paragraph);
        }

        var paragraphProperties = paragraph.Element(W + "pPr");
        paragraph.Elements().Where(e => e.Name != W + "pPr").Remove();

        if (paragraphProperties == null)
        {
            paragraph.AddFirst(new XElement(W + "pPr"));
            paragraphProperties = paragraph.Element(W + "pPr");
        }

        paragraph.Add(CreateTextRun(value));

        var additionalParagraphs = cell.Elements(W + "p").Skip(1).ToList();
        foreach (var extraParagraph in additionalParagraphs)
        {
            extraParagraph.Remove();
        }
    }

    private static XElement CreateTextRun(string? value)
    {
        return new XElement(W + "r",
            new XElement(W + "t",
                new XAttribute(XNamespace.Xml + "space", "preserve"),
                Normalize(value)));
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? " " : value.Trim();

    private static string ToMark(bool enabled) => enabled ? "X" : string.Empty;

    private static string EffectiveValue(string? preferred, string? fallback)
        => string.IsNullOrWhiteSpace(preferred) ? (fallback ?? string.Empty) : preferred;

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
