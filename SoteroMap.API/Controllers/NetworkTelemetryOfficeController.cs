using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using SoteroMap.API.Models;
using SoteroMap.API.Services;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/network-telemetry/office")]
[Tags("Network Telemetry Office")]
[Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Editor},{AppRoles.Viewer},{AppRoles.Auditor}")]
public class NetworkTelemetryOfficeController : ControllerBase
{
    private readonly NetworkTelemetryService _service;

    public NetworkTelemetryOfficeController(NetworkTelemetryService service)
    {
        _service = service;
    }

    [EndpointSummary("Resumen ejecutivo del ultimo escaneo o de un snapshot especifico")]
    [EndpointDescription("Entrega un resumen compacto para consumo interno: estado del escaneo, nivel de riesgo, cantidad de equipos detectados, usuarios conectados y ventana temporal observada.")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(NetworkTelemetryOfficeSummaryViewModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int? snapshotId = null, CancellationToken cancellationToken = default)
    {
        var dashboard = await _service.GetDashboardAsync(10, snapshotId, cancellationToken);
        if (!dashboard.HasData || dashboard.ActiveSnapshotId <= 0)
        {
            return Ok(new NetworkTelemetryOfficeSummaryViewModel
            {
                HealthLabel = dashboard.HealthLabel,
                HealthTone = dashboard.HealthTone,
                HasData = false,
                IsFresh = dashboard.IsFresh
            });
        }

        return Ok(MapSummary(dashboard));
    }

    [EndpointSummary("Listado paginado de snapshots de telemetria")]
    [EndpointDescription("Permite consultar snapshots manuales, automaticos o programados, con filtros de busqueda, dia, bloque horario y ordenamiento.")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(NetworkTelemetrySnapshotPageViewModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [HttpGet("snapshots")]
    public async Task<IActionResult> Snapshots(
        [FromQuery] string? search = null,
        [FromQuery] string? triggerType = null,
        [FromQuery] string? weekday = null,
        [FromQuery] string? timeSlot = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetSnapshotPageAsync(
            new NetworkTelemetrySnapshotQueryRequest
            {
                Search = search ?? string.Empty,
                TriggerType = triggerType ?? string.Empty,
                Weekday = weekday ?? string.Empty,
                TimeSlot = timeSlot ?? string.Empty,
                SortBy = sortBy ?? "observedAt",
                SortDirection = sortDirection ?? "desc",
                Page = page,
                PageSize = pageSize
            },
            cancellationToken);

        return Ok(result);
    }

    [EndpointSummary("Detalle consolidado de un snapshot")]
    [EndpointDescription("Devuelve el resumen del snapshot seleccionado junto con riesgo por edificio, riesgo por subred y observaciones mas riesgosas.")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(NetworkTelemetryOfficeSnapshotDetailViewModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HttpGet("snapshots/{snapshotId:int}")]
    public async Task<IActionResult> SnapshotDetail(int snapshotId, CancellationToken cancellationToken = default)
    {
        var dashboard = await _service.GetDashboardAsync(10, snapshotId, cancellationToken);
        if (!dashboard.HasData || dashboard.ActiveSnapshotId != snapshotId)
        {
            return NotFound(new { message = $"No se encontro el snapshot {snapshotId}." });
        }

        return Ok(new NetworkTelemetryOfficeSnapshotDetailViewModel
        {
            Summary = MapSummary(dashboard),
            BuildingRisk = dashboard.BuildingRiskSummaries,
            SubnetRisk = dashboard.SubnetRiskSummaries,
            TopRiskObservations = dashboard.TopRiskObservations
        });
    }

    [EndpointSummary("Equipos observados en un snapshot")]
    [EndpointDescription("Entrega una pagina de endpoints/equipos detectados en el snapshot, con filtros por texto, riesgo, edificio, subred, estado online y ordenamiento.")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(NetworkTelemetryObservationPageViewModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [HttpGet("snapshots/{snapshotId:int}/devices")]
    public async Task<IActionResult> Devices(
        int snapshotId,
        [FromQuery] string? search = null,
        [FromQuery] string? riskLevel = null,
        [FromQuery] string? buildingExternalId = null,
        [FromQuery] string? subnetCidr = null,
        [FromQuery] string? onlineState = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.GetObservationPageAsync(
            snapshotId,
            new NetworkTelemetryObservationQueryRequest
            {
                Search = search ?? string.Empty,
                RiskLevel = riskLevel ?? string.Empty,
                BuildingExternalId = buildingExternalId ?? string.Empty,
                SubnetCidr = subnetCidr ?? string.Empty,
                OnlineState = onlineState ?? string.Empty,
                ObservationType = "device",
                SortBy = sortBy ?? "risk",
                SortDirection = sortDirection ?? "desc",
                Page = page,
                PageSize = pageSize
            },
            cancellationToken);

        return Ok(result);
    }

    [EndpointSummary("Usuarios y sesiones detectadas en un snapshot")]
    [EndpointDescription("Retorna la vista de usuarios/sesiones inferidas desde el escaneo: nombre detectado, estado de sesion, riesgo, subred, ultimo visto y cantidad de dispositivos vinculados.")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(NetworkTelemetrySessionOverviewViewModel), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [HttpGet("snapshots/{snapshotId:int}/users")]
    public async Task<IActionResult> Users(int snapshotId, CancellationToken cancellationToken = default)
    {
        var result = await _service.GetSessionOverviewAsync(snapshotId, cancellationToken);
        return Ok(result);
    }

    [EndpointSummary("Riesgos principales de un snapshot")]
    [EndpointDescription("Expone las observaciones de mayor riesgo y sus agregados por subred y edificio. Util para paneles, reportes y seguimiento operativo.")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [HttpGet("snapshots/{snapshotId:int}/risks")]
    public async Task<IActionResult> Risks(int snapshotId, [FromQuery] int take = 25, CancellationToken cancellationToken = default)
    {
        var top = await _service.GetTopRiskObservationsAsync(snapshotId, take, cancellationToken);
        var subnet = await _service.GetSubnetRiskSummariesAsync(snapshotId, cancellationToken);
        var building = await _service.GetBuildingRiskSummariesAsync(snapshotId, cancellationToken);

        return Ok(new
        {
            snapshotId,
            top,
            subnet,
            building
        });
    }

    [EndpointSummary("Exportacion Excel de equipos observados")]
    [EndpointDescription("Genera un archivo Excel (.xlsx) con 4 hojas: resumen ejecutivo, activos capturados, usuarios repetidos y causas de riesgo.")]
    [Produces("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [HttpGet("snapshots/{snapshotId:int}/devices/export")]
    public async Task<IActionResult> ExportDevices(int snapshotId, CancellationToken cancellationToken = default)
    {
        var exportData = await _service.GetSnapshotExportDataAsync(snapshotId, cancellationToken);
        if (exportData == null)
        {
            return NotFound(new { message = $"No se encontro el snapshot {snapshotId}." });
        }

        var bytes = BuildExportExcel(exportData);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmm");
        var filename = $"telemetria_red_snapshot_{snapshotId}_{timestamp}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
    }

    private static NetworkTelemetryOfficeSummaryViewModel MapSummary(NetworkTelemetryDashboardViewModel dashboard)
        => new()
        {
            SnapshotId = dashboard.ActiveSnapshotId,
            HealthLabel = dashboard.HealthLabel,
            HealthTone = dashboard.HealthTone,
            IsFresh = dashboard.IsFresh,
            HasData = dashboard.HasData,
            SourceName = dashboard.LatestSourceName,
            SourceType = dashboard.LatestSourceType,
            Status = dashboard.LatestStatus,
            RiskLevel = dashboard.LatestRiskLevel,
            RiskScore = dashboard.LatestRiskScore,
            DeviceCount = dashboard.LatestDeviceCount,
            ConnectedUserCount = dashboard.LatestConnectedUserCount,
            HighRiskDeviceCount = dashboard.LatestHighRiskDeviceCount,
            MediumRiskDeviceCount = dashboard.LatestMediumRiskDeviceCount,
            LowRiskDeviceCount = dashboard.LatestLowRiskDeviceCount,
            ObservedAtUtc = dashboard.LatestObservedAtUtc,
            WindowStartUtc = dashboard.LatestWindowStartUtc,
            WindowEndUtc = dashboard.LatestWindowEndUtc,
            Notes = dashboard.Notes
        };

    private static byte[] BuildExportExcel(NetworkTelemetryExportDataViewModel data)
    {
        using var workbook = new XLWorkbook();

        var sheet1 = workbook.Worksheets.Add("Resumen_Ejecutivo");
        sheet1.Cell("A1").Value = "Indicador";
        sheet1.Cell("B1").Value = "Valor";
        sheet1.Range("A1:B1").Style.Font.Bold = true;

        var legendRows = new[]
        {
            ("Snapshot ID", data.SnapshotId.ToString()),
            ("Fuente", data.SourceName),
            ("Tipo", data.SourceType),
            ("Estado", data.Status),
            ("Riesgo Global", data.RiskLevel),
            ("Puntaje Riesgo", data.RiskScore.ToString()),
            ("Dispositivos", data.DeviceCount.ToString()),
            ("Alto Riesgo", data.HighRiskDeviceCount.ToString()),
            ("Riesgo Medio", data.MediumRiskDeviceCount.ToString()),
            ("Bajo Riesgo", data.LowRiskDeviceCount.ToString()),
            ("Usuarios Conectados", data.ConnectedUserCount.ToString()),
            ("Observado UTC", data.ObservedAtUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
            ("Ventana Inicio UTC", data.WindowStartUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
            ("Ventana Fin UTC", data.WindowEndUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "")
        };

        for (int i = 0; i < legendRows.Length; i++)
        {
            sheet1.Cell(i + 2, 1).Value = legendRows[i].Item1;
            sheet1.Cell(i + 2, 2).Value = legendRows[i].Item2;
        }

        int legendStart = legendRows.Length + 4;
        sheet1.Cell(legendStart, 1).Value = "Leyenda de niveles de riesgo";
        sheet1.Range(legendStart, 1, legendStart, 2).Style.Font.Bold = true;
        sheet1.Cell(legendStart + 1, 1).Value = "Critico (>= 70 pts)";
        sheet1.Cell(legendStart + 1, 2).Value = "Riesgo critico, requiere accion inmediata. Causas: IP o MAC duplicada, dispositivo no coincide con inventario, o combinacion de multiples factores graves (antivirus ausente + parches + offline + etc).";
        sheet1.Cell(legendStart + 2, 1).Value = "Alto (40-69 pts)";
        sheet1.Cell(legendStart + 2, 2).Value = "Riesgo alto, requiere revision prioritaria. Causas: equipo sin respuesta en red, antivirus deshabilitado o ausente, parches pendientes/desactualizados, usuario no conocido, sin asignacion a edificio, equipo fuera de dominio, identificadores incompletos, espacio en disco bajo, RDP expuesto.";
        sheet1.Cell(legendStart + 3, 1).Value = "Medio (20-39 pts)";
        sheet1.Cell(legendStart + 3, 2).Value = "Riesgo medio, monitoreo recomendado. Causas: uptime prolongado (>45 dias), servicios SMB/WMI expuestos, SSH expuesto, latencia elevada, servicio de impresion visible, sin nombre resolvible.";
        sheet1.Cell(legendStart + 4, 1).Value = "Bajo (0-19 pts)";
        sheet1.Cell(legendStart + 4, 2).Value = "Riesgo bajo, sin accion inmediata requerida. Equipo sin factores de riesgo significativos detectados.";
        sheet1.Column(1).Width = 30;
        sheet1.Column(2).Width = 90;

        var sheet2 = workbook.Worksheets.Add("Activos_Capturados");
        var deviceHeaders = new[]
        {
            "IdObservacion", "ObservationType", "ClaveExterna", "NombreDispositivo",
            "UsuarioDetectado", "MAC", "IP", "HostName", "SerialNumber", "IdInventario",
            "EstadoObservacion", "NivelRiesgo", "PuntajeRiesgo", "ObservadoUtc",
            "Categoria", "SistemaOperativo", "EnLinea", "EnDominio", "EsVM", "PingMs",
            "VersionAgente", "PuertosAbiertos", "Subred", "PerfilRed", "RiesgosDetectados"
        };

        for (int c = 0; c < deviceHeaders.Length; c++)
        {
            sheet2.Cell(1, c + 1).Value = deviceHeaders[c];
        }
        sheet2.Range(1, 1, 1, deviceHeaders.Length).Style.Font.Bold = true;

        var deviceRows = data.Devices.Select(d => new object[]
        {
            d.Id, d.ObservationType, d.ExternalKey, d.DeviceName, d.Username,
            d.MacAddress, d.IpAddress, d.HostName, d.SerialNumber, (object?)d.ImportedInventoryItemId ?? "",
            d.Status, d.RiskLevel, d.RiskScore, d.ObservedAtUtc.ToString("O"), d.DeviceCategory,
            d.OperatingSystem,
            d.IsOnline switch { true => "Si", false => "No", _ => "" },
            d.DomainJoined switch { true => "Si", false => "No", _ => "" },
            d.IsVirtualMachine switch { true => "Si", false => "No", _ => "" },
            (object?)d.PingMs ?? "", d.AgentVersion, d.OpenPorts, d.SubnetCidr, d.NetworkProfile,
            string.Join("; ", d.RiskReasons)
        });
        sheet2.Cell(2, 1).InsertData(deviceRows);

        sheet2.Column(1).Width = 14;
        sheet2.Column(2).Width = 16;
        sheet2.Column(3).Width = 20;
        sheet2.Column(4).Width = 22;
        sheet2.Column(5).Width = 20;
        sheet2.Column(6).Width = 18;
        sheet2.Column(7).Width = 16;
        sheet2.Column(8).Width = 18;
        sheet2.Column(9).Width = 16;
        sheet2.Column(10).Width = 12;
        sheet2.Column(11).Width = 18;
        sheet2.Column(12).Width = 14;
        sheet2.Column(13).Width = 12;
        sheet2.Column(14).Width = 24;
        sheet2.Column(15).Width = 16;
        sheet2.Column(16).Width = 20;
        sheet2.Column(17).Width = 10;
        sheet2.Column(18).Width = 10;
        sheet2.Column(19).Width = 8;
        sheet2.Column(20).Width = 8;
        sheet2.Column(21).Width = 14;
        sheet2.Column(22).Width = 16;
        sheet2.Column(23).Width = 16;
        sheet2.Column(24).Width = 14;
        sheet2.Column(25).Width = 40;

        var sheet3 = workbook.Worksheets.Add("Usuarios_Repetidos");
        var userHeaders = new[]
        {
            "Username", "Apariciones", "HostsDistintos", "IPsDistintas",
            "RiesgoMax", "NivelRiesgo", "TipoSospecha"
        };

        for (int c = 0; c < userHeaders.Length; c++)
        {
            sheet3.Cell(1, c + 1).Value = userHeaders[c];
        }
        sheet3.Range(1, 1, 1, userHeaders.Length).Style.Font.Bold = true;

        var userRows = data.RepeatedUsers.Select(u => new object[]
        {
            u.Username, u.Apariciones, u.HostsDistintos, u.IPsDistintas,
            u.RiesgoMax, u.NivelRiesgo, u.TipoSospecha
        });
        sheet3.Cell(2, 1).InsertData(userRows);

        sheet3.Column(1).Width = 24;
        sheet3.Column(2).Width = 14;
        sheet3.Column(3).Width = 16;
        sheet3.Column(4).Width = 14;
        sheet3.Column(5).Width = 12;
        sheet3.Column(6).Width = 14;
        sheet3.Column(7).Width = 16;

        var sheet4 = workbook.Worksheets.Add("Causas_Riesgo");
        sheet4.Cell("A1").Value = "Causa de riesgo";
        sheet4.Cell("B1").Value = "Cantidad";
        sheet4.Cell("C1").Value = "Porcentaje sobre activos";
        sheet4.Range("A1:C1").Style.Font.Bold = true;

        var causeRows = data.RiskCauses.Select(c => new object[]
        {
            c.Causa, c.Cantidad, c.Porcentaje
        });
        sheet4.Cell(2, 1).InsertData(causeRows);

        sheet4.Column(1).Width = 50;
        sheet4.Column(2).Width = 12;
        sheet4.Column(3).Width = 24;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
