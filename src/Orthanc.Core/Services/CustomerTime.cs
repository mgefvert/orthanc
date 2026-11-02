using Orthanc.Core.Entities;

namespace Orthanc.Core.Services;

public static class CustomerTime
{
    public static TimeZoneInfo Zone(DbCustomer customer) => TimeZoneInfo.FindSystemTimeZoneById(customer.TimeZone);
    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    public static DateTime ToUtc(DateOnly date, TimeSpan time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).Add(time), DateTimeKind.Unspecified);
        // A nonexistent local report time advances to the first valid minute.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        // On an autumn overlap choose the first occurrence; delivery identity prevents a repeat.
        if (zone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime;
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static (DateTime Start, DateTime End, DateTime Deadline) DailyPeriod(DbCustomer c, DateOnly date)
    {
        var zone = Zone(c);
        return (ToUtc(date, TimeSpan.Zero, zone), ToUtc(date.AddDays(1), TimeSpan.Zero, zone),
            ToUtc(date.AddDays(1), c.ReportTime, zone));
    }
}