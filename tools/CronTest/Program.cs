using Cronos;

var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
var nowUtc = DateTime.UtcNow;

Console.WriteLine($"Now UTC: {nowUtc:O}");
Console.WriteLine($"Now Santiago: {TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz):O}");
Console.WriteLine();

var simulatedUtc = new DateTime(2026, 8, 21, 12, 31, 0, DateTimeKind.Utc);
var simulatedLocal = TimeZoneInfo.ConvertTimeFromUtc(simulatedUtc, tz);
Console.WriteLine($"=== Simulating service at {simulatedUtc:O} (Santiago: {simulatedLocal:O}) ===");
Console.WriteLine();

var crons = new[]
{
    ("SEMANAL FRI 08:30", "0 30 8 * * 5"),
    ("SEMANAL FRI 13:30", "0 30 13 * * 5"),
    ("SEMANAL FRI 16:00", "0 30 16 * * 5"),
    ("SEMANAL MTWTh 08:30", "0 30 8 * * 1,2,3,4"),
    ("SEMANAL MTWTh 13:30", "0 30 13 * * 1,2,3,4"),
    ("SEMANAL MTWTh 17:30", "0 30 17 * * 1,2,3,4"),
    ("PRUEBA4 10:35 FRI", "0 35 10 * * 5"),
};

Console.WriteLine("--- Using real DateTime.UtcNow ---");
foreach (var (label, cron) in crons)
{
    PrintResult(label, cron, nowUtc, tz);
}

Console.WriteLine();
Console.WriteLine("--- Using simulated time (12:31 UTC = 08:31 Santiago) ---");
foreach (var (label, cron) in crons)
{
    PrintResult(label, cron, simulatedUtc, tz);
}

// Also test the hosted service logic
Console.WriteLine();
Console.WriteLine("=== Simulating GetDelayUntilNextRun at 12:31 UTC ===");
var allCandidates = new List<DateTimeOffset>();
var allParts = new[]
{
    ("Fri schedule", "0 30 8 * * 5;0 30 13 * * 5;0 30 16 * * 5"),
    ("Mon-Thu schedule", "0 30 8 * * 1,2,3,4;0 30 13 * * 1,2,3,4;0 30 17 * * 1,2,3,4"),
    ("PRUEBA4", "0 35 10 * * 5"),
};
foreach (var (label, cron) in allParts)
{
    var parts = cron.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var part in parts)
    {
        if (CronExpression.TryParse(part, CronFormat.IncludeSeconds, out var expr))
        {
            var next = expr.GetNextOccurrence(simulatedUtc, tz);
            if (next.HasValue)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(next.Value, tz);
                var delay = next.Value - simulatedUtc;
                Console.WriteLine($"  {label} part=\"{part}\" => Next={next.Value:O} Local={local:O} Delay={delay.TotalMinutes:F1}min");
                allCandidates.Add(new DateTimeOffset(next.Value, TimeSpan.Zero));
            }
            else
            {
                Console.WriteLine($"  {label} part=\"{part}\" => NULL");
            }
        }
        else
        {
            Console.WriteLine($"  {label} part=\"{part}\" => PARSE FAILED");
        }
    }
}
var nowDto = new DateTimeOffset(simulatedUtc, TimeSpan.Zero);
var earliest = allCandidates.OrderBy(c => c).First();
var delayResult = earliest - nowDto;
Console.WriteLine();
Console.WriteLine($"Earliest candidate: {earliest.UtcDateTime:O} => Delay: {delayResult.TotalMinutes:F1}min = {delayResult}");

static void PrintResult(string label, string cron, DateTime fromUtc, TimeZoneInfo tz)
{
    Console.Write($"{label} => cron=\"{cron}\" => ");
    var parsed = CronExpression.TryParse(cron, CronFormat.IncludeSeconds, out var expr);
    Console.Write($"Parsed={parsed}");
    if (expr != null)
    {
        var next = expr.GetNextOccurrence(fromUtc, tz);
        if (next.HasValue)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(next.Value, tz);
            var delay = next.Value - fromUtc;
            Console.Write($" => Next={next.Value:O} Local={local:O} Delay={delay.TotalMinutes:F1}min");
        }
        else
        {
            Console.Write(" => NULL");
        }
    }
    Console.WriteLine();
}
