using Cronos;

var tests = new[]
{
    (cron: "0 30 8,13,17 * * 1-4", label: "Test1a (IncludeSeconds)", format: CronFormat.IncludeSeconds),
    (cron: "0 30 8,13,17 * * 1-4", label: "Test1b (Standard)", format: CronFormat.Standard),
    (cron: "0 30 8,13,16 * * 5", label: "Test2a (IncludeSeconds)", format: CronFormat.IncludeSeconds),
    (cron: "0 30 8,13,16 * * 5", label: "Test2b (Standard)", format: CronFormat.Standard),
};

void RunTest(string cron, string label, CronFormat format, DateTime utcNow)
{
    Console.WriteLine($"=== {label} | format={format} | utc={utcNow:yyyy-MM-dd HH:mm:ss} UTC ===");
    bool parsed = CronExpression.TryParse(cron, format, out CronExpression? expr);
    Console.WriteLine($"TryParse returned: {parsed}");
    if (parsed && expr is not null)
    {
        var next = expr.GetNextOccurrence(utcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Santiago"));
        Console.WriteLine($"Next occurrence: {(next.HasValue ? next.Value.ToString("yyyy-MM-dd HH:mm:ss zzz") : "null")}");
    }
    Console.WriteLine();
}

var time1 = new DateTime(2026, 7, 2, 21, 7, 41, DateTimeKind.Utc);
var time2 = new DateTime(2026, 7, 3, 13, 56, 25, DateTimeKind.Utc);

foreach (var (cron, label, format) in tests)
{
    RunTest(cron, label, format, time1);
}

foreach (var (cron, label, format) in tests)
{
    RunTest(cron, label, format, time2);
}
