using System.Text;
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

    [EndpointSummary("Exportacion CSV de equipos observados")]
    [EndpointDescription("Genera un CSV con los equipos observados del snapshot aplicando los mismos filtros principales que la consulta paginada. Pensado para descarga interna y cruces externos.")]
    [Produces("text/csv")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [HttpGet("snapshots/{snapshotId:int}/devices/export")]
    public async Task<IActionResult> ExportDevices(
        int snapshotId,
        [FromQuery] string? search = null,
        [FromQuery] string? riskLevel = null,
        [FromQuery] string? buildingExternalId = null,
        [FromQuery] string? subnetCidr = null,
        [FromQuery] string? onlineState = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null,
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
                Page = 1,
                PageSize = 200
            },
            cancellationToken);

        var csv = BuildDevicesCsv(result.Items);
        var bytes = Encoding.UTF8.GetBytes(csv);
        return File(bytes, "text/csv; charset=utf-8", $"network-telemetry-snapshot-{snapshotId}-devices.csv");
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

    private static string BuildDevicesCsv(IReadOnlyList<NetworkTelemetryObservationViewModel> items)
    {
        static string Escape(string? value)
        {
            var normalized = value ?? string.Empty;
            if (normalized.Contains('"'))
            {
                normalized = normalized.Replace("\"", "\"\"");
            }

            if (normalized.IndexOfAny([',', '"', '\n', '\r']) >= 0)
            {
                normalized = $"\"{normalized}\"";
            }

            return normalized;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Id,Tipo,Usuario,Host,Equipo,IP,MAC,Serie,Subred,Edificio,Riesgo,Puntaje,Online,Puertos,SO,Fabricante,Modelo,UltimaObservacionUtc");
        foreach (var item in items)
        {
            builder.Append(Escape(item.Id.ToString())).Append(',')
                .Append(Escape(item.ObservationType)).Append(',')
                .Append(Escape(item.Username)).Append(',')
                .Append(Escape(item.HostName)).Append(',')
                .Append(Escape(item.DeviceName)).Append(',')
                .Append(Escape(item.IpAddress)).Append(',')
                .Append(Escape(item.MacAddress)).Append(',')
                .Append(Escape(item.SerialNumber)).Append(',')
                .Append(Escape(item.SubnetCidr)).Append(',')
                .Append(Escape(item.BuildingExternalId)).Append(',')
                .Append(Escape(item.RiskLevel)).Append(',')
                .Append(Escape(item.RiskScore.ToString())).Append(',')
                .Append(Escape(item.IsOnline?.ToString() ?? string.Empty)).Append(',')
                .Append(Escape(item.OpenPorts)).Append(',')
                .Append(Escape(item.OperatingSystem)).Append(',')
                .Append(Escape(item.Manufacturer)).Append(',')
                .Append(Escape(item.Model)).Append(',')
                .Append(Escape(item.ObservedAtUtc.ToString("O")))
                .AppendLine();
        }

        return builder.ToString();
    }
}
