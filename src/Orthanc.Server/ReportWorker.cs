using DotNetCommons.Services.Email;
using Microsoft.EntityFrameworkCore;
using Orthanc.Core;
using Orthanc.Core.Entities;
using Orthanc.Core.Services;

namespace Orthanc.Server;

public sealed class ReportWorker(IServiceScopeFactory scopes, TimeProvider clock, IConfiguration config,
    ILogger<ReportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Reports:Enabled", true))
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try
            {
                await Tick(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Report worker pass failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task Tick(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<OrthancContext>();

        var customers = await db.Customers.AsNoTracking()
            .Where(x => x.Enabled && x.ReportRecipients != "")
            .ToListAsync(ct);

        foreach (var customer in customers)
        {
            try
            {
                var now = clock.GetUtcNow().UtcDateTime;
                if (customer.ReportTime < TimeSpan.Zero || customer.ReportTime >= TimeSpan.FromDays(1))
                    throw new InvalidOperationException("ReportTime must be a local time of day");

                var today   = CustomerTime.LocalDate(now, CustomerTime.Zone(customer));
                var created = CustomerTime.LocalDate(customer.CreatedZ, CustomerTime.Zone(customer));

                // Bounded restart catch-up. No reports for days before customer creation.
                for (var day = today.AddDays(-7); day < today; day = day.AddDays(1))
                {
                    if (day < created)
                        continue;

                    var period = CustomerTime.DailyPeriod(customer, day);
                    if (now < period.Deadline)
                        continue;

                    await Deliver(scope.ServiceProvider, customer, "daily", period.Start, period.End, async () =>
                    {
                        var jobs = await db.Jobs.AsNoTracking()
                            .Where(x => x.CustomerId == customer.Id)
                            .ToListAsync(ct);

                        var runs = await db.JobRuns.AsNoTracking()
                            .Where(x => x.Job.CustomerId == customer.Id && x.StartedZ >= period.Start && x.StartedZ < period.End)
                            .ForReport()
                            .ToListAsync(ct);

                        return ReportService.Daily(customer, day, period.Deadline, jobs, runs);
                    }, ct);
                }

                if (customer.HourlyAlerts)
                {
                    var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
                    for (var end = hour.AddHours(-5); end <= hour; end = end.AddHours(1))
                    {
                        var start = end.AddHours(-1);
                        if (end <= customer.CreatedZ)
                            continue;

                        var timeEnd = end;
                        await Deliver(scope.ServiceProvider, customer, "hourly", start, end, async () =>
                        {
                            var runs = await db.JobRuns.AsNoTracking()
                                .Where(x => x.Job.CustomerId == customer.Id
                                            && x.CompletedReceivedZ >= start && x.CompletedReceivedZ < timeEnd
                                            && (x.ErrorCount > 0 || x.FatalCount > 0))
                                .ForReport()
                                .ToListAsync(ct);

                            return runs.Count == 0 ? null : ReportService.Hourly(customer, start, end, runs);
                        }, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reports failed for customer {CustomerId}", customer.Id);
                db.ChangeTracker.Clear();
            }
        }

        var retention = config.GetValue("RetentionDays", 30);
        if (retention < 8)
            throw new InvalidOperationException("RetentionDays must be at least 8 for report catch-up");

        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-retention);

        // Retain active runs. Limit each deletion pass to avoid long locks.
        var old = await db.JobRuns
            .Where(x => x.StartedZ < cutoff && x.CompletedReceivedZ < cutoff)
            .OrderBy(x => x.StartedZ)
            .Select(x => x.Id)
            .Take(1000)
            .ToListAsync(ct);

        if (old.Count > 0)
            await db.JobRuns
                .Where(x => old.Contains(x.Id))
                .ExecuteDeleteAsync(ct);

        await db.ReportDeliveries
            .Where(x => x.PeriodEndZ < cutoff && x.SentZ != null)
            .ExecuteDeleteAsync(ct);
    }

    private async Task Deliver(IServiceProvider services, DbCustomer customer, string kind, DateTime start, DateTime end,
        Func<Task<ReportContent?>> build, CancellationToken ct)
    {
        var db = services.GetRequiredService<OrthancContext>();

        var delivery = await db.ReportDeliveries
            .SingleOrDefaultAsync(x => x.CustomerId == customer.Id && x.Kind == kind && x.PeriodStartZ == start, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        if (delivery?.SentZ != null || delivery?.LastAttemptZ > now.AddMinutes(-5))
            return;

        if (delivery == null)
        {
            delivery = new DbReportDelivery
            {
                CustomerId   = customer.Id,
                Kind         = kind,
                PeriodStartZ = start,
                PeriodEndZ   = end
            };
            db.ReportDeliveries.Add(delivery);
        }

        delivery.LastAttemptZ = now;
        delivery.Attempts++;
        await db.SaveChangesAsync(ct);

        var report = await build();
        if (report != null)
        {
            using var message = ReportService.Message(customer,
                config["Reports:From"] ?? throw new InvalidOperationException("Reports:From is required"), report);

            var email = services.GetRequiredService<IEmailIntegration>();

            var result = await email.SendAsync([message], cancellationToken: ct);
            if (result.Count != 1 || result[0].Result != Result.Success)
                throw new InvalidOperationException("Email delivery failed; will retry after five minutes");

            logger.LogInformation("Sent {ReportKind} report for customer {CustomerId}, period {PeriodStartZ}", kind, customer.Id, start);
        }

        delivery.SentZ = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }
}
