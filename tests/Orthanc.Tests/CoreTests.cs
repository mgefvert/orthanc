using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Orthanc.Core;
using Orthanc.Core.Entities;
using Orthanc.Core.Services;

namespace Orthanc.Tests;

[TestClass]
public sealed class CoreTests
{
    private static readonly ClassifierService ClassifierService = new(NullLogger<ClassifierService>.Instance);
    private static CompleteRequest Completion(int? exit = 0, params string[] lines) =>
        new(DateTimeOffset.UtcNow, 1, exit, lines.Select(x => new LogLine("stdout", x)).ToList());

    [TestMethod]
    public void FatalWinsOverExitCodeAndWarning()
    {
        var result = ClassifierService.Classify(Completion(1, "WARNING: look", "panic now", "an error"), []);
        Assert.AreEqual(Outcome.Fatal, result.Outcome);
        Assert.AreEqual(1, result.WarningCount);
        Assert.AreEqual(1, result.ErrorCount);
        Assert.AreEqual(1, result.FatalCount);
    }

    [TestMethod]
    public void FirstRulePerLineAndJobExclusionsOverrideCustomerRules()
    {
        var result = ClassifierService.Classify(Completion(0, "0 errors", "WARNING and ERROR"),
        [
            new() { CustomerId = 1, Regex = "errors?", Severity = Severity.Error },
            new() { CustomerId = 1, JobId = 4, Regex = "^0 errors$", Severity = Severity.Ignore }
        ]);
        
        Assert.AreEqual(Outcome.Failed, result.Outcome);
        Assert.AreEqual(1, result.ErrorCount);
        Assert.AreEqual(0, result.WarningCount);
    }

    [TestMethod]
    public void UnknownStdinIsNotSuccessButCanDetectErrors()
    {
        Assert.AreEqual(Outcome.Unknown, ClassifierService.Classify(Completion(null, "ordinary output"), []).Outcome);
        Assert.AreEqual(Outcome.Failed, ClassifierService.Classify(Completion(null, "ERROR: broke"), []).Outcome);
    }

    [TestMethod]
    public void IgnoringExitCodeDoesNotIgnoreTimeoutOrLogErrors()
    {
        Assert.AreEqual(Outcome.Success, ClassifierService.Classify(Completion(3) with { IgnoreExitCode = true }, []).Outcome);
        Assert.AreEqual(Outcome.Failed, ClassifierService.Classify(Completion(3, "error") with { IgnoreExitCode = true }, []).Outcome);
        Assert.AreEqual(Outcome.Failed, ClassifierService.Classify(Completion(0) with { TimedOut = true }, []).Outcome);
    }

    [TestMethod]
    public void InvalidRegexAndTruncatedOutputCannotClaimCleanSuccess()
    {
        var bad = ClassifierService.Classify(Completion(), [new() { Regex = "[" }]);
        Assert.IsTrue(bad.Limited);
        Assert.AreEqual(Outcome.Unknown, bad.Outcome);
        Assert.AreEqual(Outcome.Unknown, ClassifierService.Classify(Completion() with { Truncated = true }, []).Outcome);
    }

    [TestMethod]
    public void CustomerDayUsesActualDstLength()
    {
        var c = new DbCustomer { TimeZone = "America/New_York", ReportTime = TimeSpan.FromHours(3) };
        var spring = CustomerTime.DailyPeriod(c, new(2026, 3, 8));
        var autumn = CustomerTime.DailyPeriod(c, new(2026, 11, 1));
        Assert.AreEqual(23d, (spring.End - spring.Start).TotalHours);
        Assert.AreEqual(25d, (autumn.End - autumn.Start).TotalHours);
        Assert.AreEqual(new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc), autumn.Deadline);
    }

    [TestMethod]
    public void InvalidAndAmbiguousReportTimesHaveDeterministicResolution()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.AreEqual(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc),
            CustomerTime.ToUtc(new(2026, 3, 8), TimeSpan.FromHours(2.5), zone));
        Assert.AreEqual(new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc),
            CustomerTime.ToUtc(new(2026, 11, 1), TimeSpan.FromHours(1.5), zone));
    }

    [TestMethod]
    public void ReportHonorsThreeAmDeadlineMissingJobsAndHtmlEscaping()
    {
        var day = new DateOnly(2026, 11, 1);
        var c = new DbCustomer { Name = "<Customer>", ReportTime = TimeSpan.FromHours(3) };
        var period = CustomerTime.DailyPeriod(c, day);
        var jobs = new List<DbJob>
        {
            new() { Id = 1, MachineName = "host", JobName = "missing", ExpectedDailyFrom = day },
            new() { Id = 2, MachineName = "host", JobName = "manual" },
            new() { Id = 3, MachineName = "host", JobName = "backup", ExpectedDailyFrom = day },
            new() { Id = 4, MachineName = "host", JobName = "hang", ExpectedDailyFrom = day }
        };
        var runs = new List<DbJobRun>
        {
            new() { JobId = 3, StartedZ = period.End.AddHours(-1), ReceivedZ = period.End.AddHours(-1),
                FinishedZ = period.End.AddHours(2), Outcome = Outcome.Failed, ErrorText = "<script>alert(1)</script>" },
            new() { JobId = 4, StartedZ = period.End.AddHours(-1), ReceivedZ = period.End.AddHours(-1),
                FinishedZ = period.End.AddHours(4), Outcome = Outcome.Success }
        };
        var report = ReportService.Daily(c, day, period.Deadline, jobs, runs);
        Assert.Contains("Missing: 1", report.Text);
        Assert.Contains("Incomplete: 1", report.Text);
        Assert.Contains("Failed: 1", report.Text);
        Assert.DoesNotContain("host/manual", report.Text);
        Assert.IsLessThan(report.Text.IndexOf("Failed: host/backup", StringComparison.Ordinal), report.Text.IndexOf("Missing: host/missing", StringComparison.Ordinal));
        Assert.DoesNotContain("<script>", report.Html);
        Assert.Contains("&lt;script&gt;", report.Html);
        using var message = ReportService.Message(withRecipients(), "sender@example.com", report);
        Assert.HasCount(2, message.AlternateViews);

        DbCustomer withRecipients() { c.ReportRecipients = "recipient@example.com"; return c; }
    }

    [TestMethod]
    public async Task IngestionIsTenantScopedAndCompletionIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new OrthancContext(new DbContextOptionsBuilder<OrthancContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Customers.Add(new() { Id = 1, CreatedZ = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var ingestion = new IngestionService(db, TimeProvider.System, ClassifierService);
        var run = await ingestion.Start(1, new("HOST", "Backup", DateTimeOffset.UtcNow.AddMinutes(-1)), "127.0.0.1", default);
        Assert.IsNull(run.Job.ExpectedDailyFrom);
        Assert.AreEqual("host", run.Job.MachineName);
        var request = Completion(9, "warning here");
        var crossTenant = await Assert.ThrowsAsync<ProtocolException>(() => ingestion.Complete(2, run.Id, request, default));
        Assert.AreEqual(404, crossTenant.Status);
        var completed = await ingestion.Complete(1, run.Id, request, default);
        Assert.AreEqual(9, completed.ExitCode);
        Assert.AreEqual(Outcome.Failed, completed.Outcome);
        await ingestion.Complete(1, run.Id, request, default);
        Assert.AreEqual(1, await db.JobRuns.CountAsync());
        var conflict = await Assert.ThrowsAsync<ProtocolException>(() => ingestion.Complete(1, run.Id, request with { ExitCode = 0 }, default));
        Assert.AreEqual(409, conflict.Status);
        var reportRuns = await db.JobRuns.ForReport().ToListAsync();
        Assert.HasCount(1, reportRuns);
        Assert.AreEqual("", reportRuns[0].LogText);
    }

    [TestMethod]
    public async Task OversizedLogsAreRejectedBeforePersistence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new OrthancContext(new DbContextOptionsBuilder<OrthancContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Customers.Add(new() { Id = 1 }); await db.SaveChangesAsync();
        var ingestion = new IngestionService(db, TimeProvider.System, ClassifierService);
        var run = await ingestion.Start(1, new("host", "job", DateTimeOffset.UtcNow.AddSeconds(-1)), "", default);
        var request = Completion(0, new string('x', IngestionService.MaxLineChars + 1));
        var error = await Assert.ThrowsAsync<ProtocolException>(() => ingestion.Complete(1, run.Id, request, default));
        Assert.AreEqual(400, error.Status);
        Assert.IsNull(run.FinishedZ);
    }
}
