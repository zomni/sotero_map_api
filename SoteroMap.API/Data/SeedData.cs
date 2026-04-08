using SoteroMap.API.Data;
using SoteroMap.API.Models;

namespace SoteroMap.API.Data;

public static class SeedData
{
    public static async Task InitializeAsync(AppDbContext context)
    {
        if (context.Locations.Any()) return;

        var locations = new List<Location>
        {
            new() { Name = "Sala de Servidores",   Campus = "sotero", Floor = "0", Type = "lab",    Latitude = -33.4570, Longitude = -70.6480, Description = "Sala principal de servidores" },
            new() { Name = "Laboratorio Redes",     Campus = "sotero", Floor = "0", Type = "lab",    Latitude = -33.4572, Longitude = -70.6482, Description = "Lab de redes y telecomunicaciones" },
            new() { Name = "Sala de Reuniones A",   Campus = "sotero", Floor = "1", Type = "room",   Latitude = -33.4574, Longitude = -70.6484, Description = "Sala de reuniones primer piso" },
            new() { Name = "Oficina Informática",   Campus = "sotero", Floor = "1", Type = "office", Latitude = -33.4576, Longitude = -70.6486, Description = "Oficina del área de informática" },
            new() { Name = "Aula Magna",            Campus = "sotero", Floor = "0", Type = "common", Latitude = -33.4578, Longitude = -70.6488, Description = "Auditorio principal" },
            new() { Name = "Biblioteca",            Campus = "sotero", Floor = "1", Type = "common", Latitude = -33.4580, Longitude = -70.6490, Description = "Biblioteca y sala de estudio" },
        };

        context.Locations.AddRange(locations);
        await context.SaveChangesAsync();

        var equipments = new List<Equipment>
        {
            new() { Name = "Servidor Dell PowerEdge R740", Category = "Server",    SerialNumber = "SRV-001", Status = "active",      LocationId = locations[0].Id },
            new() { Name = "Servidor HP ProLiant DL380",   Category = "Server",    SerialNumber = "SRV-002", Status = "active",      LocationId = locations[0].Id },
            new() { Name = "Switch Cisco Catalyst 2960",   Category = "Network",   SerialNumber = "NET-001", Status = "active",      LocationId = locations[1].Id },
            new() { Name = "Router Mikrotik RB4011",       Category = "Network",   SerialNumber = "NET-002", Status = "active",      LocationId = locations[1].Id },
            new() { Name = "PC Lenovo ThinkCentre M90",    Category = "PC",        SerialNumber = "PC-001",  Status = "active",      LocationId = locations[1].Id },
            new() { Name = "Proyector Epson EB-X51",       Category = "Projector", SerialNumber = "PRJ-001", Status = "active",      LocationId = locations[2].Id },
            new() { Name = "PC Dell OptiPlex 7090",        Category = "PC",        SerialNumber = "PC-002",  Status = "maintenance", LocationId = locations[3].Id, Notes = "En mantención preventiva" },
            new() { Name = "Impresora HP LaserJet M404",   Category = "Printer",   SerialNumber = "PRT-001", Status = "active",      LocationId = locations[3].Id },
            new() { Name = "Proyector Benq MX550",         Category = "Projector", SerialNumber = "PRJ-002", Status = "active",      LocationId = locations[4].Id },
            new() { Name = "Sistema de Audio JBL",         Category = "Audio",     SerialNumber = "AUD-001", Status = "active",      LocationId = locations[4].Id },
        };

        context.Equipments.AddRange(equipments);
        await context.SaveChangesAsync();
    }
}
