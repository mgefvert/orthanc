using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Orthanc.Core;
using Orthanc.Core.Entities;
using Orthanc.Core.Services;

namespace Orthanc.Admin.Commands;

public abstract class ReportCommand : AdminCommand
{
    protected async Task<(DbCustomer Customer, ReportContent Report)> Build(OrthancContext context, CancellationToken ct)
    {
        var c = await GetArgsCustomer(context, ct);
        var date = Args.Date == null
            ? CustomerTime.LocalDate(DateTime.UtcNow, CustomerTime.Zone(c)).AddDays(-1)
            : DateOnly.ParseExact(Args.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var period = CustomerTime.DailyPeriod(c, date);
        var jobs   = await context.Jobs.AsNoTracking().Where(x => x.CustomerId == c.Id).ToListAsync(ct);
        var runs = await context.JobRuns.AsNoTracking()
            .Where(x => x.Job.CustomerId == c.Id && x.StartedZ >= period.Start && x.StartedZ < period.End)
            .ForReport().ToListAsync(ct);

        return (c, ReportService.Daily(c, date, period.Deadline, jobs, runs));
    }
}