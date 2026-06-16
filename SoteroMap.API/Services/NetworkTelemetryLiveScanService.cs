using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Services;

public class NetworkTelemetryLiveScanService
{
    private readonly AppDbContext _context;
    private readonly NetworkTelemetryService _networkTelemetryService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NetworkTelemetryLiveScanService> _logger;

    public NetworkTelemetryLiveScanService(
        AppDbContext context,
        NetworkTelemetryService networkTelemetryService,
        IConfiguration configuration,
        ILogger<NetworkTelemetryLiveScanService> logger)
    {
        _context = context;
        _networkTelemetryService = networkTelemetryService;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsEnabled()
        => GetBool("NetworkTelemetrySettings:Enabled", "NETWORK_TELEMETRY_ENABLED", true);

    public bool AutoScanEnabled()
        => GetBool("NetworkTelemetrySettings:AutoScanEnabled", "NETWORK_TELEMETRY_AUTO_SCAN_ENABLED", true);

    public async Task<NetworkTelemetryIngestResultViewModel> ScanAndStoreAsync(
        string createdByUsername,
        NetworkTelemetryLiveScanRequest? options,
        CancellationToken cancellationToken = default)
    {
        var request = await BuildLiveRequestAsync(options, cancellationToken);
        var actor = string.IsNullOrWhiteSpace(createdByUsername) ? "system" : createdByUsername.Trim();

        _logger.LogInformation(
            "Starting live network telemetry scan against {SubnetCount} subnets with {DeviceCount} discovered candidates.",
            request.Notes,
            request.Devices.Count);

        var result = await _networkTelemetryService.IngestAsync(request, actor, cancellationToken);

        _logger.LogInformation(
            "Live network telemetry scan finished. Snapshot {SnapshotId}, devices {DeviceCount}, risk {RiskLevel} ({RiskScore}).",
            result.SnapshotId,
            result.DeviceCount,
            result.OverallRiskLevel,
            result.OverallRiskScore);

        return result;
    }

    public async Task<NetworkTelemetryIngestRequest> BuildLiveRequestAsync(
        NetworkTelemetryLiveScanRequest? options = null,
        CancellationToken cancellationToken = default)
    {
        var ranges = (await ResolveScanRangesAsync(cancellationToken)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var maxRanges = GetInt("NetworkTelemetrySettings:MaxRangesPerScan", "NETWORK_TELEMETRY_MAX_RANGES_PER_SCAN", 12);
        if (maxRanges > 0)
        {
            ranges = ranges.Take(maxRanges).ToList();
        }
        var ports = ResolvePorts();
        var maxHosts = GetInt("NetworkTelemetrySettings:MaxHostsPerScan", "NETWORK_TELEMETRY_MAX_HOSTS_PER_SCAN", 4096);
        var concurrentHosts = GetInt("NetworkTelemetrySettings:MaxConcurrentProbes", "NETWORK_TELEMETRY_MAX_CONCURRENT_PROBES", 96);
        var tcpTimeoutMs = GetInt("NetworkTelemetrySettings:TcpTimeoutMs", "NETWORK_TELEMETRY_TCP_TIMEOUT_MS", 250);
        var pingTimeoutMs = GetInt("NetworkTelemetrySettings:PingTimeoutMs", "NETWORK_TELEMETRY_PING_TIMEOUT_MS", 250);
        var dnsTimeoutMs = GetInt("NetworkTelemetrySettings:DnsTimeoutMs", "NETWORK_TELEMETRY_DNS_TIMEOUT_MS", 1200);

        var candidates = new List<IPAddress>();
        foreach (var range in ranges)
        {
            foreach (var ip in EnumerateHosts(range).Take(Math.Max(1, maxHosts)))
            {
                candidates.Add(ip);
            }
        }

        candidates = candidates
            .Distinct()
            .OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? ToUInt32(ip) : 0)
            .ToList();

        var results = new List<NetworkTelemetryDeviceInput>();
        var throttle = new SemaphoreSlim(Math.Max(1, concurrentHosts));
        var tasks = candidates.Select(async ip =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var device = await ProbeHostAsync(
                    ip,
                    ports,
                    pingTimeoutMs,
                    tcpTimeoutMs,
                    dnsTimeoutMs,
                    cancellationToken);

                if (device is not null)
                {
                    lock (results)
                    {
                        results.Add(device);
                    }
                }
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        var macMap = await BuildMacMapAsync(cancellationToken);
        foreach (var device in results)
        {
            if (string.IsNullOrWhiteSpace(device.MacAddress) && macMap.TryGetValue(device.IpAddress, out var mac))
            {
                device.MacAddress = mac;
            }

            if (string.IsNullOrWhiteSpace(device.SubnetCidr))
            {
                device.SubnetCidr = ResolveSubnetForIp(device.IpAddress, ranges);
            }

            if (string.IsNullOrWhiteSpace(device.NetworkProfile))
            {
                device.NetworkProfile = InferNetworkProfile(device.DeviceCategory, ParseOpenPorts(device.OpenPorts), device.HostName, device.DeviceName);
            }

            if (string.IsNullOrWhiteSpace(device.Manufacturer))
            {
                device.Manufacturer = InferManufacturer(device.MacAddress, device.NetworkProfile, device.DeviceCategory);
            }

            if (string.IsNullOrWhiteSpace(device.Model))
            {
                device.Model = InferModel(device.NetworkProfile, device.DeviceCategory, ParseOpenPorts(device.OpenPorts));
            }
        }

        var sessionWarnings = new List<string>();
        if (options?.ResolveInteractiveSessions != false)
        {
            sessionWarnings.AddRange(await EnrichDevicesWithInteractiveSessionsAsync(results, options, cancellationToken));
        }

        var liveUsers = BuildLiveUsers(results);

        var discoveredRanges = ranges.Count == 0
            ? "sin rangos configurados"
            : string.Join(", ", ranges);

        var notes = new StringBuilder($"Rangos: {discoveredRanges}. Hosts detectados: {results.Count}. Identidades de red: {liveUsers.Count}.");
        if (sessionWarnings.Count > 0)
        {
            notes.Append(' ');
            notes.Append(string.Join(" | ", sessionWarnings.Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        return new NetworkTelemetryIngestRequest
        {
            SourceName = GetString("NetworkTelemetrySettings:SourceName", "NETWORK_TELEMETRY_SOURCE_NAME", "Escaneo vivo de red"),
            SourceType = "live-scan",
            ObservedAtUtc = DateTime.UtcNow,
            WindowStartUtc = DateTime.UtcNow.AddMinutes(-Math.Max(1, GetInt("NetworkTelemetrySettings:FreshnessMinutes", "NETWORK_TELEMETRY_FRESHNESS_MINUTES", 30))),
            WindowEndUtc = DateTime.UtcNow,
            Notes = notes.ToString(),
            Devices = results
                .OrderBy(device => device.IpAddress, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Users = liveUsers
        };
    }

    private IReadOnlyList<int> ResolvePorts()
    {
        var configured = GetString("NetworkTelemetrySettings:ScanPorts", "NETWORK_TELEMETRY_SCAN_PORTS", "135,139,445,3389,80,443,22,9100,631,515,53,88,389,161,5985,5986,8080,8443");
        var ports = configured
            .Split(new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : -1)
            .Where(port => port > 0 && port < 65536)
            .Distinct()
            .ToList();

        return ports.Count == 0
            ? new List<int> { 135, 139, 445, 3389, 80, 443, 22, 9100, 631, 515, 53, 88, 389, 161, 5985, 5986, 8080, 8443 }
            : ports;
    }

    private static IReadOnlyList<int> ParseOpenPorts(string? value)
    {
        var ports = new List<int>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return ports;
        }

        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0 && port < 65536)
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    private async Task<NetworkTelemetryDeviceInput?> ProbeHostAsync(
        IPAddress ip,
        IReadOnlyList<int> ports,
        int pingTimeoutMs,
        int tcpTimeoutMs,
        int dnsTimeoutMs,
        CancellationToken cancellationToken)
    {
        var pingMs = await TryPingAsync(ip, pingTimeoutMs, cancellationToken);
        var openPorts = await ProbeOpenPortsAsync(ip, ports, tcpTimeoutMs, cancellationToken);
        var hostName = await ResolveHostNameAsync(ip, dnsTimeoutMs, cancellationToken);

        var online = pingMs.HasValue || openPorts.Count > 0 || !string.IsNullOrWhiteSpace(hostName);
        if (!online)
        {
            return null;
        }

        var deviceName = BuildDeviceName(hostName, ip, openPorts);
        var category = InferCategory(openPorts, hostName, deviceName);

        return new NetworkTelemetryDeviceInput
        {
            ExternalKey = $"live:{ip}",
            DeviceName = deviceName,
            Username = string.Empty,
            Domain = InferDomain(hostName),
            IpAddress = ip.ToString(),
            MacAddress = string.Empty,
            SerialNumber = string.Empty,
            HostName = hostName ?? string.Empty,
            DeviceCategory = category,
            OperatingSystem = InferOperatingSystem(openPorts, hostName),
            OperatingSystemVersion = string.Empty,
            Manufacturer = InferManufacturer(string.Empty, InferNetworkProfile(category, openPorts, hostName, deviceName), category),
            Model = InferModel(InferNetworkProfile(category, openPorts, hostName, deviceName), category, openPorts),
            Processor = string.Empty,
            MemoryGb = null,
            DiskTotalGb = null,
            DiskFreeGb = null,
            LastBootAtUtc = null,
            IsOnline = true,
            DomainJoined = IsDomainLike(hostName),
            IsVirtualMachine = null,
            PingMs = pingMs,
            AntivirusStatus = string.Empty,
            AntivirusVersion = string.Empty,
            PatchStatus = string.Empty,
            AgentVersion = "live-scan",
            OpenPorts = string.Join(",", openPorts.OrderBy(port => port)),
            SubnetCidr = string.Empty,
            NetworkProfile = InferNetworkProfile(category, openPorts, hostName, deviceName),
            BuildingExternalId = string.Empty,
            RoomExternalId = string.Empty,
            Status = "observed",
            Notes = string.Empty
        };
    }

    private async Task<int?> TryPingAsync(IPAddress ip, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, timeoutMs);
            if (reply.Status == IPStatus.Success)
            {
                return (int)Math.Max(1, reply.RoundtripTime);
            }
        }
        catch
        {
        }

        return null;
    }

    private async Task<List<int>> ProbeOpenPortsAsync(IPAddress ip, IReadOnlyList<int> ports, int timeoutMs, CancellationToken cancellationToken)
    {
        var openPorts = new List<int>();
        if (ports.Count == 0)
        {
            return openPorts;
        }

        var throttle = new SemaphoreSlim(Math.Min(ports.Count, Math.Max(1, GetInt("NetworkTelemetrySettings:PortProbeConcurrency", "NETWORK_TELEMETRY_PORT_PROBE_CONCURRENCY", 12))));
        var tasks = ports.Select(async port =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                if (await IsTcpPortOpenAsync(ip, port, timeoutMs, cancellationToken))
                {
                    lock (openPorts)
                    {
                        openPorts.Add(port);
                    }
                }
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        return openPorts;
    }

    private static async Task<bool> IsTcpPortOpenAsync(IPAddress ip, int port, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(ip, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs, cancellationToken));
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> ResolveHostNameAsync(IPAddress ip, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            var lookupTask = Dns.GetHostEntryAsync(ip);
            var completed = await Task.WhenAny(lookupTask, Task.Delay(timeoutMs, cancellationToken));
            if (completed == lookupTask)
            {
                var entry = await lookupTask;
                return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName.Trim();
            }
        }
        catch
        {
        }

        return null;
    }

    private async Task<Dictionary<string, string>> BuildMacMapAsync(CancellationToken cancellationToken)
    {
        var commands = OperatingSystem.IsWindows()
            ? new[] { new[] { "arp", "-a" } }
            : new[]
            {
                new[] { "ip", "neigh" },
                new[] { "arp", "-a" }
            };

        foreach (var command in commands)
        {
            var output = await RunCommandAsync(command[0], command.Skip(1).ToArray(), cancellationToken);
            var map = ParseMacTable(output);
            if (map.Count > 0)
            {
                return map;
            }
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseMacTable(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return map;
        }

        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length >= 2)
            {
                if (IPAddress.TryParse(tokens[0], out var ip) && TryNormalizeMac(tokens, out var mac))
                {
                    map[ip.ToString()] = mac;
                    continue;
                }
            }

            var ipIndex = line.IndexOf("lladdr", StringComparison.OrdinalIgnoreCase);
            if (ipIndex >= 0)
            {
                var before = line[..ipIndex].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var after = line[(ipIndex + 6)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (before.Length > 0 && after.Length > 0 && IPAddress.TryParse(before[0], out var parsedIp))
                {
                    map[parsedIp.ToString()] = NormalizeMac(after[0]);
                }
            }
        }

        return map;
    }

    private static bool TryNormalizeMac(string[] tokens, out string mac)
    {
        foreach (var token in tokens)
        {
            if (token.Contains('-', StringComparison.Ordinal) || token.Contains(':', StringComparison.Ordinal))
            {
                mac = NormalizeMac(token);
                return mac.Length >= 11;
            }
        }

        mac = string.Empty;
        return false;
    }

    private static string NormalizeMac(string value)
        => value.Trim().Replace('-', ':').ToUpperInvariant();

    private async Task<string> RunCommandAsync(string fileName, string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return string.Empty;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            return string.IsNullOrWhiteSpace(output) ? error : output;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildDeviceName(string? hostName, IPAddress ip, IReadOnlyList<int> openPorts)
    {
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            return hostName!;
        }

        if (openPorts.Contains(9100) || openPorts.Contains(631) || openPorts.Contains(515))
        {
            return $"impresora-{ip}";
        }

        if (openPorts.Contains(3389) || openPorts.Contains(445) || openPorts.Contains(135))
        {
            return $"pc-{ip}";
        }

        return ip.ToString();
    }

    private static string InferCategory(IReadOnlyList<int> openPorts, string? hostName, string deviceName)
    {
        var composite = $"{hostName} {deviceName}".ToUpperInvariant();
        if (openPorts.Contains(9100) || openPorts.Contains(631) || openPorts.Contains(515) ||
            composite.Contains("PRINTER", StringComparison.OrdinalIgnoreCase) ||
            composite.Contains("IMPRES", StringComparison.OrdinalIgnoreCase))
        {
            return "impresora";
        }

        if (openPorts.Contains(3389) || openPorts.Contains(445) || openPorts.Contains(135))
        {
            return "pc";
        }

        if (openPorts.Contains(22))
        {
            return "servidor";
        }

        return "other";
    }

    private static string InferNetworkProfile(string category, IReadOnlyList<int> openPorts, string? hostName, string? deviceName)
    {
        var composite = $"{category} {hostName} {deviceName}".ToUpperInvariant();
        if (category == "impresora" || openPorts.Contains(9100) || openPorts.Contains(631) || openPorts.Contains(515) ||
            composite.Contains("PRINTER", StringComparison.OrdinalIgnoreCase) ||
            composite.Contains("IMPRES", StringComparison.OrdinalIgnoreCase))
        {
            return "printer";
        }

        if (openPorts.Contains(3389) || openPorts.Contains(445) || openPorts.Contains(135) || openPorts.Contains(5985) || openPorts.Contains(5986))
        {
            return "workstation";
        }

        if (openPorts.Contains(22) || openPorts.Contains(389) || openPorts.Contains(88))
        {
            return "server";
        }

        if (openPorts.Contains(161) || openPorts.Contains(53))
        {
            return "infrastructure";
        }

        return string.IsNullOrWhiteSpace(category) ? "network" : category;
    }

    private static string InferOperatingSystem(IReadOnlyList<int> openPorts, string? hostName)
    {
        if (openPorts.Contains(3389) || openPorts.Contains(445) || openPorts.Contains(135))
        {
            return "Windows";
        }

        if (openPorts.Contains(22))
        {
            return "Linux/Unix";
        }

        if (openPorts.Contains(9100) || openPorts.Contains(631) || openPorts.Contains(515))
        {
            return "Printer";
        }

        return string.IsNullOrWhiteSpace(hostName) ? "Unknown" : "Unknown";
    }

    private static string InferManufacturer(string macAddress, string networkProfile, string category)
    {
        var normalized = NormalizeMac(macAddress);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return networkProfile switch
            {
                "printer" => "Impresion",
                "workstation" => "PC",
                "server" => "Servidor",
                "infrastructure" => "Infraestructura",
                _ => string.IsNullOrWhiteSpace(category) ? string.Empty : category
            };
        }

        var oui = normalized.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3)
            .Select(part => part.Trim().ToUpperInvariant())
            .ToArray();
        if (oui.Length < 3)
        {
            return string.Empty;
        }

        var prefix = string.Join(':', oui);
        return prefix switch
        {
            "00:1B:78" or "00:1E:8C" or "00:17:C8" or "00:15:99" => "HP",
            "00:1A:4B" or "00:21:5A" or "F4:CE:46" => "Dell",
            "00:1C:25" or "00:50:56" or "00:05:69" => "Lenovo",
            "00:0D:93" or "00:25:90" => "Ricoh",
            "00:1D:7E" or "FC:FB:FB" => "Brother",
            "00:40:01" or "00:80:77" => "Canon",
            "00:13:50" or "00:1F:16" => "Epson",
            "00:08:74" or "00:0F:1F" => "Samsung",
            "00:15:5D" => "Microsoft",
            "00:1A:11" => "Intel",
            "3C:52:82" => "Zebra",
            "00:1C:B3" => "Cisco",
            "FC:EC:DA" => "Ubiquiti",
            _ => string.Empty
        };
    }

    private static string InferModel(string networkProfile, string category, IReadOnlyList<int> openPorts)
    {
        if (networkProfile == "printer" || category == "impresora")
        {
            return "network-printer";
        }

        if (networkProfile == "server" || category == "servidor")
        {
            return "server";
        }

        if (networkProfile == "workstation" || category == "pc")
        {
            return "workstation";
        }

        return openPorts.Contains(161) ? "network-device" : string.Empty;
    }

    private static string InferDomain(string? hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return string.Empty;
        }

        var parts = hostName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 1 ? string.Join('.', parts.Skip(1)) : string.Empty;
    }

    private static bool IsDomainLike(string? hostName)
    {
        return !string.IsNullOrWhiteSpace(hostName) && hostName.Contains('.', StringComparison.Ordinal);
    }

    private async Task<List<string>> ResolveScanRangesAsync(CancellationToken cancellationToken)
    {
        var ranges = new List<string>();
        var configured = GetString("NetworkTelemetrySettings:ScanCidrs", "NETWORK_TELEMETRY_SCAN_CIDRS", string.Empty);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var item in configured.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsValidCidr(item))
                {
                    ranges.Add(item);
                }
            }

            return ranges;
        }

        ranges.AddRange(DiscoverLdapAnchorRanges());
        ranges.AddRange(DiscoverLocalRanges());

        return ranges
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<NetworkTelemetryUserInput> BuildLiveUsers(IReadOnlyList<NetworkTelemetryDeviceInput> devices)
    {
        return devices
            .GroupBy(device => BuildEndpointIdentity(device), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group
                    .OrderByDescending(device => device.PingMs ?? 0)
                    .ThenByDescending(device => device.DeviceName)
                    .First();

                return new NetworkTelemetryUserInput
                {
                    ExternalKey = $"network:{BuildEndpointIdentity(latest)}",
                    Username = BuildEndpointIdentity(latest),
                    DisplayName = ResolveDisplayName(latest),
                    DeviceCount = group.Count(),
                    Status = ResolveNetworkStatus(latest),
                    Notes = BuildNetworkNotes(group.ToList())
                };
            })
            .ToList();
    }

    private static string BuildNetworkNotes(IReadOnlyList<NetworkTelemetryDeviceInput> devices)
    {
        var notes = new List<string>();
        var latest = devices
            .OrderByDescending(device => device.PingMs ?? 0)
            .ThenByDescending(device => device.DeviceName)
            .First();

        if (!string.IsNullOrWhiteSpace(latest.NetworkProfile))
        {
            notes.Add($"perfil {latest.NetworkProfile}");
        }

        if (!string.IsNullOrWhiteSpace(latest.SubnetCidr))
        {
            notes.Add($"subred {latest.SubnetCidr}");
        }

        if (latest.PingMs.HasValue)
        {
            notes.Add($"latencia {latest.PingMs.Value} ms");
        }

        if (!string.IsNullOrWhiteSpace(latest.OpenPorts))
        {
            notes.Add($"puertos {latest.OpenPorts}");
        }

        return string.Join(" | ", notes);
    }

    private static string ResolveSubnetForIp(string ipAddress, IReadOnlyList<string> ranges)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return string.Empty;
        }

        foreach (var range in ranges)
        {
            if (IsIpInCidr(ip, range))
            {
                return range;
            }
        }

        return ToSubnet24(ipAddress);
    }

    private static bool IsIpInCidr(IPAddress ip, string cidr)
    {
        var parts = cidr.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp) || !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        if (baseIp.AddressFamily != AddressFamily.InterNetwork || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return (ToUInt32(ip) & mask) == (ToUInt32(baseIp) & mask);
    }

    private static string BuildEndpointIdentity(NetworkTelemetryDeviceInput device)
    {
        var candidates = new[]
        {
            device.Username,
            device.HostName,
            device.DeviceName,
            device.IpAddress
        };

        return candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "endpoint";
    }

    private static string ResolveDisplayName(NetworkTelemetryDeviceInput device)
    {
        if (!string.IsNullOrWhiteSpace(device.Username))
        {
            return device.Username;
        }

        if (!string.IsNullOrWhiteSpace(device.DeviceName))
        {
            return device.DeviceName;
        }

        if (!string.IsNullOrWhiteSpace(device.HostName))
        {
            return device.HostName;
        }

        return device.IpAddress;
    }

    private static string ResolveNetworkStatus(NetworkTelemetryDeviceInput device)
    {
        if (device.Status.Equals("active", StringComparison.OrdinalIgnoreCase) ||
            device.Status.Equals("disconnected", StringComparison.OrdinalIgnoreCase))
        {
            return device.Status;
        }

        if (device.IsOnline == true)
        {
            return "active";
        }

        if (device.PingMs is null && string.IsNullOrWhiteSpace(device.OpenPorts))
        {
            return "expired";
        }

        return "observed";
    }

    private async Task<IReadOnlyList<string>> EnrichDevicesWithInteractiveSessionsAsync(
        IReadOnlyList<NetworkTelemetryDeviceInput> devices,
        NetworkTelemetryLiveScanRequest? options,
        CancellationToken cancellationToken)
    {
        if (devices.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (!OperatingSystem.IsWindows())
        {
            return new[]
            {
                "Sesion real omitida: esta captura corre sobre Linux/Docker y la consulta remota Windows requiere ejecutar el backend en Windows."
            };
        }

        var credential = BuildProbeCredential(options);
        var windowsCandidates = devices
            .Where(IsWindowsSessionCandidate)
            .ToList();

        if (windowsCandidates.Count == 0)
        {
            return Array.Empty<string>();
        }

        var resolved = 0;
        var errors = 0;
        foreach (var device in windowsCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var session = await TryResolveInteractiveSessionAsync(device, credential, cancellationToken);
                if (session is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(session.Username))
                {
                    device.Username = session.Username;
                    resolved++;
                }

                if (!string.IsNullOrWhiteSpace(session.Status))
                {
                    device.Status = session.Status;
                }

                if (!string.IsNullOrWhiteSpace(session.Notes))
                {
                    device.Notes = string.IsNullOrWhiteSpace(device.Notes)
                        ? session.Notes
                        : $"{device.Notes} | {session.Notes}";
                }
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogDebug(ex, "No fue posible resolver la sesion interactiva para {Host}.", device.HostName);
            }
        }

        var warnings = new List<string>();
        if (resolved > 0)
        {
            warnings.Add($"Sesion real capturada en {resolved} equipos Windows.");
        }
        else if (options?.ResolveInteractiveSessions == true)
        {
            warnings.Add("No se logro capturar ninguna sesion real. Revisa permisos remotos, WMI/WinRM y el contexto Windows del backend.");
        }

        if (errors > 0)
        {
            warnings.Add($"Hubo {errors} equipos sin respuesta al consultar sesion remota.");
        }

        return warnings;
    }

    private static bool IsWindowsSessionCandidate(NetworkTelemetryDeviceInput device)
    {
        if (device is null)
        {
            return false;
        }

        var category = device.DeviceCategory ?? string.Empty;
        var operatingSystem = device.OperatingSystem ?? string.Empty;
        var openPorts = ParseOpenPorts(device.OpenPorts);

        return category.Equals("pc", StringComparison.OrdinalIgnoreCase)
            || category.Equals("servidor", StringComparison.OrdinalIgnoreCase)
            || operatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase)
            || openPorts.Contains(3389)
            || openPorts.Contains(445)
            || openPorts.Contains(135);
    }

    private ProbeCredential? BuildProbeCredential(NetworkTelemetryLiveScanRequest? options)
    {
        if (options is null ||
            string.IsNullOrWhiteSpace(options.DirectoryUsername) ||
            string.IsNullOrWhiteSpace(options.DirectoryPassword))
        {
            return null;
        }

        var domain = string.IsNullOrWhiteSpace(options.DirectoryDomain)
            ? GetString("LdapSettings:Domain", "LDAP_DOMAIN", "SSMSO")
            : options.DirectoryDomain.Trim();

        return new ProbeCredential(options.DirectoryUsername.Trim(), options.DirectoryPassword, domain);
    }

    private async Task<InteractiveSessionResult?> TryResolveInteractiveSessionAsync(
        NetworkTelemetryDeviceInput device,
        ProbeCredential? credential,
        CancellationToken cancellationToken)
    {
        var host = !string.IsNullOrWhiteSpace(device.HostName)
            ? device.HostName.Trim()
            : device.IpAddress.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var quserResult = await TryResolveInteractiveSessionWithQuserAsync(host, cancellationToken);
        if (quserResult is not null)
        {
            return quserResult;
        }

        return TryResolveInteractiveSessionWithWmi(host, credential);
    }

    private async Task<InteractiveSessionResult?> TryResolveInteractiveSessionWithQuserAsync(string host, CancellationToken cancellationToken)
    {
        var output = await RunCommandAsync("quser", new[] { $"/server:{host}" }, cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var parsed = ParseQuserOutput(output);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Username))
        {
            return null;
        }

        return parsed;
    }

    private InteractiveSessionResult? TryResolveInteractiveSessionWithWmi(string host, ProbeCredential? credential)
    {
        try
        {
            var options = new ConnectionOptions();
            if (credential is not null)
            {
                options.Username = credential.QualifiedUsername;
                options.Password = credential.Password;
                options.EnablePrivileges = true;
                options.Impersonation = ImpersonationLevel.Impersonate;
                options.Authentication = AuthenticationLevel.PacketPrivacy;
            }

            var scope = new ManagementScope($@"\\{host}\root\cimv2", options);
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT UserName FROM Win32_ComputerSystem"));
            foreach (ManagementObject item in searcher.Get())
            {
                var rawUser = item["UserName"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(rawUser))
                {
                    continue;
                }

                var username = rawUser.Contains('\\', StringComparison.Ordinal)
                    ? rawUser[(rawUser.LastIndexOf('\\') + 1)..]
                    : rawUser;

                return new InteractiveSessionResult(username, "active", $"WMI: {rawUser}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WMI remoto no disponible para {Host}.", host);
        }

        return null;
    }

    private static InteractiveSessionResult? ParseQuserOutput(string output)
    {
        var lines = output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .ToList();

        foreach (var line in lines)
        {
            var collapsed = line.Replace(">", string.Empty, StringComparison.Ordinal).Trim();
            if (string.IsNullOrWhiteSpace(collapsed))
            {
                continue;
            }

            var tokens = collapsed
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            var username = tokens[0].Trim();
            if (string.IsNullOrWhiteSpace(username) || username.Equals("USERNAME", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = collapsed.ToUpperInvariant();
            var status = normalized.Contains(" ACTIVE ", StringComparison.Ordinal) || normalized.EndsWith(" ACTIVE", StringComparison.Ordinal)
                ? "active"
                : normalized.Contains(" DISC ", StringComparison.Ordinal) || normalized.Contains(" DISCONECT", StringComparison.Ordinal)
                    ? "disconnected"
                    : "observed";

            return new InteractiveSessionResult(username, status, $"QUSER: {collapsed}");
        }

        return null;
    }

    private sealed record ProbeCredential(string Username, string Password, string Domain)
    {
        public string QualifiedUsername =>
            Username.Contains('\\', StringComparison.Ordinal) || Username.Contains('@', StringComparison.Ordinal)
                ? Username
                : $"{Domain}\\{Username}";
    }

    private sealed record InteractiveSessionResult(string Username, string Status, string Notes);

    private IEnumerable<string> DiscoverLdapAnchorRanges()
    {
        var anchor = GetString("LdapSettings:FallbackHost", "LDAP_FALLBACK_HOST", string.Empty);
        if (IsPrivateIpv4(anchor))
        {
            yield return ToSubnet24(anchor);
        }

        var host = GetString("LdapSettings:Host", "LDAP_HOST", string.Empty);
        if (IsPrivateIpv4(host))
        {
            yield return ToSubnet24(host);
        }
    }

    private static IEnumerable<string> DiscoverLocalRanges()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var props = nic.GetIPProperties();
            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var prefix = unicast.PrefixLength;
                if (prefix <= 0 || prefix > 32)
                {
                    prefix = 24;
                }

                yield return $"{unicast.Address}/{prefix}";
            }
        }
    }

    private static bool IsValidCidr(string value)
    {
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               IPAddress.TryParse(parts[0], out _) &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix) &&
               prefix is >= 0 and <= 32;
    }

    private static bool IsPrivateIpv4(string? value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();
        return bytes[0] switch
        {
            10 => true,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            192 when bytes[1] == 168 => true,
            _ => false
        };
    }

    private static string ToSubnet24(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return value;
        }

        var bytes = ip.GetAddressBytes();
        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
    }

    private static IEnumerable<IPAddress> EnumerateHosts(string cidr)
    {
        var parts = cidr.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp) || !int.TryParse(parts[1], out var prefixLength))
        {
            yield break;
        }

        if (baseIp.AddressFamily != AddressFamily.InterNetwork)
        {
            yield break;
        }

        var baseAddress = ToUInt32(baseIp);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var network = baseAddress & mask;
        var broadcast = network | ~mask;

        if (broadcast <= network + 1)
        {
            yield break;
        }

        for (var address = network + 1; address < broadcast; address++)
        {
            yield return FromUInt32(address);
        }
    }

    private static uint ToUInt32(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToUInt32(bytes, 0);
    }

    private static IPAddress FromUInt32(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return new IPAddress(bytes);
    }

    private bool GetBool(string configKey, string envKey, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return bool.TryParse(_configuration[configKey], out parsed) ? parsed : fallback;
    }

    private int GetInt(string configKey, string envKey, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        if (int.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return int.TryParse(_configuration[configKey], out parsed) ? parsed : fallback;
    }

    private string GetString(string configKey, string envKey, string fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw!;
        }

        return string.IsNullOrWhiteSpace(_configuration[configKey]) ? fallback : _configuration[configKey]!;
    }
}
