using System.Text.RegularExpressions;
using Cronos;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Services;

public class TelemetryScanScheduleService
{
    private const string DefaultTimeZone = "America/Santiago";

    private static readonly Dictionary<string, string> DayToCron = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lu"] = "1", ["ma"] = "2", ["mi"] = "3", ["ju"] = "4",
        ["vi"] = "5", ["sa"] = "6", ["do"] = "0"
    };

    private static readonly Dictionary<string, string> CronToDay = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1"] = "lu", ["2"] = "ma", ["3"] = "mi", ["4"] = "ju",
        ["5"] = "vi", ["6"] = "sa", ["0"] = "do"
    };

    private static readonly string[] DayOrder = ["lu", "ma", "mi", "ju", "vi", "sa", "do"];

    private readonly AppDbContext _context;

    public TelemetryScanScheduleService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<TelemetryScanScheduleDto>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var schedules = await _context.TelemetryScanSchedules
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var nowUtc = DateTime.UtcNow;
        return schedules.Select(s => ToDto(s, nowUtc)).ToList();
    }

    public async Task<TelemetryScanScheduleDto> CreateAsync(
        TelemetryScanScheduleRequest request,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        var schedule = new TelemetryScanSchedule();
        Apply(schedule, request);
        schedule.CreatedAtUtc = DateTime.UtcNow;

        _context.TelemetryScanSchedules.Add(schedule);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(schedule, DateTime.UtcNow);
    }

    public async Task<TelemetryScanScheduleDto?> UpdateAsync(
        Guid id,
        TelemetryScanScheduleRequest request,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        var schedule = await _context.TelemetryScanSchedules
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null, cancellationToken);

        if (schedule is null)
        {
            return null;
        }

        Apply(schedule, request);
        schedule.UpdatedAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(schedule, DateTime.UtcNow);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var schedule = await _context.TelemetryScanSchedules
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null, cancellationToken);

        if (schedule is null)
        {
            return false;
        }

        schedule.DeletedAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<List<string>> DetectOverlapsAsync(
        string cron,
        Guid? excludeId,
        CancellationToken cancellationToken = default)
    {
        var newSlots = ParseSlotsFromCron(cron);
        var newPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in newSlots)
        {
            foreach (var day in slot.Days)
            {
                newPairs.Add($"{slot.Time}|{day}");
            }
        }

        var existing = await _context.TelemetryScanSchedules
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && s.IsEnabled)
            .ToListAsync(cancellationToken);

        var overlaps = new List<string>();

        foreach (var sched in existing)
        {
            if (excludeId.HasValue && sched.Id == excludeId.Value) continue;

            var existingSlots = ParseSlotsFromCron(sched.Cron);
            foreach (var slot in existingSlots)
            {
                foreach (var day in slot.Days)
                {
                    var key = $"{slot.Time}|{day}";
                    if (newPairs.Contains(key))
                    {
                        overlaps.Add($"\"{sched.Label}\" ({slot.Time} {day})");
                    }
                }
            }
        }

        return overlaps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string BuildCronFromSlots(List<ScheduleSlotDto> slots)
    {
        if (slots == null || slots.Count == 0)
        {
            return string.Empty;
        }

        var grouped = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Time) || slot.Days == null || slot.Days.Count == 0)
            {
                continue;
            }

            var timeParts = slot.Time.Split(':');
            if (timeParts.Length != 2 || !int.TryParse(timeParts[0], out var hour) || !int.TryParse(timeParts[1], out var minute))
            {
                continue;
            }

            var timeKey = $"{hour}:{minute}";

            if (!grouped.ContainsKey(timeKey))
            {
                grouped[timeKey] = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (var day in slot.Days)
            {
                if (DayToCron.TryGetValue(day.Trim(), out var cronDay))
                {
                    grouped[timeKey].Add(cronDay);
                }
            }
        }

        var cronParts = new List<string>();

        foreach (var kvp in grouped)
        {
            var timeParts = kvp.Key.Split(':');
            var hour = int.Parse(timeParts[0]);
            var minute = int.Parse(timeParts[1]);
            var cronDays = string.Join(",", kvp.Value.OrderBy(d => d, StringComparer.Ordinal));
            cronParts.Add($"0 {minute} {hour} * * {cronDays}");
        }

        return string.Join(";", cronParts);
    }

    public static List<ScheduleSlotDto> ParseSlotsFromCron(string cron)
    {
        var result = new List<ScheduleSlotDto>();
        if (string.IsNullOrWhiteSpace(cron))
        {
            return result;
        }

        var expressions = cron.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var expr in expressions)
        {
            if (!TryParseCron(expr, out var parsed) || parsed is null)
            {
                continue;
            }

            var cronFields = expr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            string minuteField;
            string hourField;
            string dayOfWeek;

            if (cronFields.Length == 6)
            {
                minuteField = cronFields[1];
                hourField = cronFields[2];
                dayOfWeek = cronFields[5];
            }
            else if (cronFields.Length == 5)
            {
                minuteField = cronFields[0];
                hourField = cronFields[1];
                dayOfWeek = cronFields[4];
            }
            else
            {
                continue;
            }

            var minutes = ExpandCronNumbers(minuteField);
            var hours = ExpandCronNumbers(hourField);
            var days = ExpandCronDays(dayOfWeek);

            foreach (var m in minutes)
            {
                foreach (var h in hours)
                {
                    result.Add(new ScheduleSlotDto
                    {
                        Time = $"{h:D2}:{m:D2}",
                        Days = new List<string>(days)
                    });
                }
            }
        }

        return result;
    }

    public static string GenerateLabelFromSlots(List<ScheduleSlotDto> slots)
    {
        if (slots == null || slots.Count == 0)
        {
            return "Sin horarios";
        }

        var timeDayMap = new Dictionary<string, List<string>>();
        foreach (var slot in slots)
        {
            foreach (var d in slot.Days)
            {
                var label = DayToCron.ContainsKey(d) ? d : d;
                if (!timeDayMap.ContainsKey(d))
                {
                    timeDayMap[d] = new List<string>();
                }
                timeDayMap[d].Add(slot.Time);
            }
        }

        var groups = new List<List<string>>();
        var current = new List<string>();
        foreach (var d in DayOrder)
        {
            if (timeDayMap.ContainsKey(d))
            {
                current.Add(d);
            }
            else
            {
                if (current.Count > 0)
                {
                    groups.Add(new List<string>(current));
                    current = new List<string>();
                }
            }
        }
        if (current.Count > 0)
        {
            groups.Add(current);
        }

        var parts = groups.Select(g =>
        {
            var dayStr = g.Count >= 3
                ? $"{g[0]}-{g[g.Count - 1]}"
                : string.Join(", ", g);
            var times = string.Join(", ", g.SelectMany(d => timeDayMap[d]).Distinct().OrderBy(t => t));
            return $"{dayStr} {times}";
        });

        return string.Join(" | ", parts);
    }

    public static bool TryParseCron(string? cron, out CronExpression? expression)
    {
        expression = null;
        if (string.IsNullOrWhiteSpace(cron))
        {
            return false;
        }

        if (CronExpression.TryParse(cron, CronFormat.IncludeSeconds, out expression))
        {
            return true;
        }

        return CronExpression.TryParse(cron, CronFormat.Standard, out expression);
    }

    public static bool IsValidCompoundCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            return false;
        }

        var parts = cron.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (!TryParseCron(part, out _))
            {
                return false;
            }
        }

        return true;
    }

    public static DateTime? GetNextOccurrenceUtc(string cron, string timeZoneId, DateTime fromUtc)
    {
        var timeZone = ResolveTimeZone(timeZoneId);
        var normalizedFrom = NormalizeUtc(fromUtc);

        var parts = cron.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DateTime? earliest = null;

        foreach (var part in parts)
        {
            if (!TryParseCron(part, out var expression) || expression is null)
            {
                continue;
            }

            var nextUtc = expression.GetNextOccurrence(normalizedFrom, timeZone);
            if (nextUtc.HasValue && (!earliest.HasValue || nextUtc.Value < earliest.Value))
            {
                earliest = nextUtc.Value;
            }
        }

        return earliest;
    }

    public static IReadOnlyList<DateTime> GetNextOccurrencesUtc(string cron, string timeZoneId, DateTime fromUtc, int count)
    {
        var results = new List<DateTime>();
        if (count <= 0)
        {
            return results;
        }

        var timeZone = ResolveTimeZone(timeZoneId);
        var cursor = NormalizeUtc(fromUtc);
        var parts = cron.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var expressions = new List<CronExpression>();
        foreach (var part in parts)
        {
            if (TryParseCron(part, out var expr) && expr is not null)
            {
                expressions.Add(expr);
            }
        }

        if (expressions.Count == 0)
        {
            return results;
        }

        for (var i = 0; i < count; i++)
        {
            DateTime? nextUtc = null;
            foreach (var expression in expressions)
            {
                var candidate = expression.GetNextOccurrence(cursor, timeZone);
                if (candidate.HasValue && (!nextUtc.HasValue || candidate.Value < nextUtc.Value))
                {
                    nextUtc = candidate.Value;
                }
            }

            if (nextUtc is null)
            {
                break;
            }

            results.Add(nextUtc.Value);
            cursor = nextUtc.Value.AddSeconds(1);
        }

        return results;
    }

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch
            {
            }
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZone);
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }

    public static string ResolveScheduleTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                return timeZoneId.Trim();
            }
            catch
            {
            }
        }

        return DefaultTimeZone;
    }

    private static void Apply(TelemetryScanSchedule schedule, TelemetryScanScheduleRequest request)
    {
        string cron;

        if (request.Slots is { Count: > 0 })
        {
            cron = BuildCronFromSlots(request.Slots);
        }
        else
        {
            cron = (request.Cron ?? string.Empty).Trim();
        }

        if (!IsValidCompoundCron(cron))
        {
            throw new InvalidOperationException("No se pudo generar una expresion cron valida. Verifique los horarios seleccionados.");
        }

        schedule.Label = string.IsNullOrWhiteSpace(request.Label) ? GenerateLabelFromSlots(ParseSlotsFromCron(cron)) : request.Label.Trim();
        schedule.Cron = cron;
        schedule.TimeZone = ResolveScheduleTimeZone(request.TimeZone);
        schedule.IsEnabled = request.IsEnabled;
        schedule.SortOrder = request.SortOrder;
    }

    private static TelemetryScanScheduleDto ToDto(TelemetryScanSchedule schedule, DateTime nowUtc)
    {
        var isValid = IsValidCompoundCron(schedule.Cron);
        DateTime? nextOccurrenceUtc = null;
        DateTime? nextOccurrenceLocal = null;

        if (isValid && schedule.IsEnabled)
        {
            nextOccurrenceUtc = GetNextOccurrenceUtc(schedule.Cron, schedule.TimeZone, nowUtc);
            if (nextOccurrenceUtc.HasValue)
            {
                nextOccurrenceLocal = TimeZoneInfo.ConvertTimeFromUtc(nextOccurrenceUtc.Value, ResolveTimeZone(schedule.TimeZone));
            }
        }

        var slots = ParseSlotsFromCron(schedule.Cron);

        return new TelemetryScanScheduleDto
        {
            Id = schedule.Id,
            Label = schedule.Label,
            Cron = schedule.Cron,
            TimeZone = schedule.TimeZone,
            IsEnabled = schedule.IsEnabled,
            SortOrder = schedule.SortOrder,
            IsValid = isValid,
            ValidationError = isValid ? string.Empty : "Expresion cron invalida.",
            NextOccurrenceUtc = nextOccurrenceUtc,
            NextOccurrenceLocal = nextOccurrenceLocal,
            ScheduleSlots = slots
        };
    }

    private static List<int> ExpandCronNumbers(string field)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(field) || field == "*")
        {
            return result;
        }

        var parts = field.Split(',');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Contains('-'))
            {
                var range = trimmed.Split('-');
                if (range.Length == 2
                    && int.TryParse(range[0], out var start)
                    && int.TryParse(range[1], out var end))
                {
                    for (var i = start; i <= end; i++)
                    {
                        result.Add(i);
                    }
                }
            }
            else if (int.TryParse(trimmed, out var val))
            {
                result.Add(val);
            }
        }

        return result.Distinct().OrderBy(x => x).ToList();
    }

    private static List<string> ExpandCronDays(string dayField)
    {
        var result = new List<string>();

        if (string.IsNullOrWhiteSpace(dayField) || dayField == "*")
        {
            return new List<string> { "lu", "ma", "mi", "ju", "vi", "sa", "do" };
        }

        var parts = dayField.Split(',');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Contains('-'))
            {
                var range = trimmed.Split('-');
                if (range.Length == 2
                    && int.TryParse(range[0], out var start)
                    && int.TryParse(range[1], out var end))
                {
                    for (var i = start; ; i++)
                    {
                        var dayNum = i % 7;
                        if (CronToDay.TryGetValue(dayNum.ToString(), out var dayName))
                        {
                            result.Add(dayName);
                        }
                        if (dayNum == end) break;
                    }
                }
            }
            else
            {
                if (CronToDay.TryGetValue(trimmed, out var dayName))
                {
                    result.Add(dayName);
                }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Local)
        {
            return value.ToUniversalTime();
        }

        if (value.Kind == DateTimeKind.Unspecified)
        {
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        return value;
    }
}
