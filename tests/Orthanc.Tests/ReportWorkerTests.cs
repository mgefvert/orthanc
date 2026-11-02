using System.Net.Mail;
using DotNetCommons.Services.Email;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orthanc.Core;
using Orthanc.Core.Entities;
using Orthanc.Server;

namespace Orthanc.Tests;

[TestClass]
public sealed class ReportWorkerTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Email : IEmailIntegration
    {
        public bool FailNext { get; set; }
        public int Attempts { get; private set; }
        public List<string> Bodies { get; } = [];
        public string GetEmailFromKey(string key) => "sender@example.com";

        public Task<List<MailMessageResult>> SendAsync(List<MailMessage> messages, string? fromEmailOrKey = null, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (FailNext)
            {
                FailNext = false;
                return Task.FromResult(messages.Select(m => new MailMessageResult(m) { Result = Result.RetriableFailure }).ToList());
            }
            foreach (var m in messages)
            {
                Assert.HasCount(2, m.AlternateViews);
                Bodies.Add(m.Body);
            }
            return Task.FromResult(messages.Select(m => new MailMessageResult(m) { Result = Result.Success }).ToList());
        }

        public async Task<MailMessageResult> SendToAdminAsync(MailMessage message, string? fromEmailOrKey = null, CancellationToken cancellationToken = default)
        {
            return (await SendAsync([message], fromEmailOrKey, cancellationToken))[0];
        }
    }

    private static ServiceProvider Services(SqliteConnection connection, Email email) => new ServiceCollection()
        .AddDbContext<OrthancContext>(o => o.UseSqlite(connection))
        .AddSingleton<IEmailIntegration>(email)
        .BuildServiceProvider();

    private static ReportWorker Worker(ServiceProvider provider, Clock clock)
    {
        return new ReportWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Reports:From"] = "sender@example.com" })
                .Build(),
            NullLogger<ReportWorker>.Instance);
    }

    [TestMethod]
    public async Task DailyDeliveryRetriesThenSurvivesWorkerRestartWithoutDuplicates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var email = new Email { FailNext = true };
        using var services = Services(connection, email);
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrthancContext>(); await db.Database.EnsureCreatedAsync();
            db.Customers.Add(new() { Id = 1, CreatedZ = new(2026, 11, 1), ReportRecipients = "ops@example.com", ReportTime = TimeSpan.FromHours(3), HourlyAlerts = false });
            db.Jobs.Add(new() { CustomerId = 1, MachineName = "host", JobName = "missing", ExpectedDailyFrom = new(2026, 11, 1) });
            await db.SaveChangesAsync();
        }

        var clock = new Clock(new(2026, 11, 2, 2, 59, 0, TimeSpan.Zero));
        await Worker(services, clock).Tick(default);
        Assert.AreEqual(0, email.Attempts);
        clock.Now = clock.Now.AddMinutes(2);
        await Worker(services, clock).Tick(default);
        Assert.AreEqual(1, email.Attempts);
        Assert.IsEmpty(email.Bodies);
        clock.Now = clock.Now.AddMinutes(2);
        await Worker(services, clock).Tick(default);
        Assert.AreEqual(1, email.Attempts);
        clock.Now = clock.Now.AddMinutes(4);
        await Worker(services, clock).Tick(default);
        Assert.AreEqual(2, email.Attempts);
        Assert.Contains("Missing: host/missing", email.Bodies.Single());
        await Worker(services, clock).Tick(default);
        Assert.AreEqual(2, email.Attempts);
        using var check = services.CreateScope();
        var delivery = await check.ServiceProvider.GetRequiredService<OrthancContext>().ReportDeliveries.SingleAsync();
        Assert.IsNotNull(delivery.SentZ);
        Assert.AreEqual(2, delivery.Attempts);
    }

    [TestMethod]
    public async Task HourlyDigestUsesCompletionReceiptAndExcludesExitOnlyFailures()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var email = new Email();

        using var services = Services(connection, email);
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrthancContext>();
            await db.Database.EnsureCreatedAsync();

            db.Customers.Add(new() { Id = 1, CreatedZ = new(2026, 11, 1), ReportRecipients = "ops@example.com", ReportTime = TimeSpan.FromHours(4) });
            var job = new DbJob { CustomerId = 1, MachineName = "host", JobName = "late" };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();

            db.JobRuns.Add(new()
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                StartedZ = new(2026, 10, 30),
                ReceivedZ = new(2026, 10, 30),
                FinishedZ = new(2026, 11, 1),
                CompletedReceivedZ = new(2026, 11, 2, 2, 30, 0),
                Outcome = Outcome.Fatal,
                FatalCount = 1,
                ErrorText = "panic: delayed submission"
            });
            db.JobRuns.Add(new()
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                StartedZ = new(2026, 11, 2, 2, 0, 0),
                ReceivedZ = new(2026, 11, 2, 2, 0, 0),
                FinishedZ = new(2026, 11, 2, 2, 30, 0),
                CompletedReceivedZ = new(2026, 11, 2, 2, 30, 0),
                Outcome = Outcome.Failed,
                ExitCode = 7
            });
            await db.SaveChangesAsync();
        }

        var clock = new Clock(new(2026, 11, 2, 3, 1, 0, TimeSpan.Zero));
        await Worker(services, clock).Tick(default);
        Assert.HasCount(1, email.Bodies);
        Assert.Contains("panic: delayed submission", email.Bodies[0]);
        Assert.Contains("Failed: 0", email.Bodies[0]);
        await Worker(services, clock).Tick(default);
        Assert.HasCount(1, email.Bodies);
    }
}
