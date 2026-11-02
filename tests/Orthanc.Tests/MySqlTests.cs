using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using Orthanc.Core;
using Orthanc.Core.Entities;
using Orthanc.Core.Services;

namespace Orthanc.Tests;

[TestClass]
public sealed class MySqlTests
{
    [TestMethod]
    [TestCategory("MySql")]
    public async Task ManualSchemaSupportsOracleProviderAndReportQueries()
    {
        var server = Environment.GetEnvironmentVariable("ORTHANC_TEST_MYSQL");
        if (string.IsNullOrEmpty(server))
        {
            Assert.Inconclusive("Set ORTHANC_TEST_MYSQL to an isolated test MySQL server connection string."); return;
        }
        
        var cs = new MySqlConnectionStringBuilder(server) { Database = "" };
        await using var connection = new MySqlConnection(cs.ConnectionString);
        await connection.OpenAsync();
        
        var name = "orthanc_test_" + Guid.NewGuid().ToString("N");
        await new MySqlCommand($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4", connection).ExecuteNonQueryAsync();
        
        try
        {
            await connection.ChangeDatabaseAsync(name);
            var schema = Path.Combine(AppContext.BaseDirectory, "schema.sql");
            new MySqlScript(connection, await File.ReadAllTextAsync(schema)).Execute();
            cs.Database = name;
            await using var db = new OrthancContext(new DbContextOptionsBuilder<OrthancContext>().UseMySQL(cs.ConnectionString).Options);
            var customer = new DbCustomer { Name = "Integration", CreatedZ = DateTime.UtcNow, ReportTime = TimeSpan.FromHours(3) };
            db.Customers.Add(customer); await db.SaveChangesAsync();
            db.ApiKeys.Add(new() { CustomerId = customer.Id, KeyHash = Credentials.Hash(Credentials.Generate()), CreatedZ = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var ingestion = new IngestionService(db, TimeProvider.System, new(NullLogger<ClassifierService>.Instance));
            var run = await ingestion.Start(customer.Id, new("HOST", "backup", DateTimeOffset.UtcNow.AddMinutes(-1)), "::1", default);
            var completion = new CompleteRequest(DateTimeOffset.UtcNow, 60, 2, [new("stderr", "error: disk full")]);
            await ingestion.Complete(customer.Id, run.Id, completion, default);
            await ingestion.Complete(customer.Id, run.Id, completion, default);
            db.ChangeTracker.Clear();
            var saved = await db.JobRuns.SingleAsync();
            Assert.AreEqual(run.Id, saved.Id);
            Assert.AreEqual(Outcome.Failed, saved.Outcome);
            Assert.AreEqual(2, saved.ExitCode);
            Assert.AreEqual(1, saved.ErrorCount);
            Assert.AreEqual(TimeSpan.FromHours(3), (await db.Customers.SingleAsync()).ReportTime);
            var report = await db.JobRuns.ForReport().ToListAsync();
            Assert.Contains("disk full", report.Single().ErrorText);
            var job = await db.Jobs.SingleAsync(); job.ExpectedDailyFrom = new DateOnly(2026, 11, 1);
            db.ReportDeliveries.Add(new() { CustomerId = customer.Id, PeriodStartZ = DateTime.UtcNow.Date, PeriodEndZ = DateTime.UtcNow.Date.AddDays(1) });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var expectedDate = new DateOnly(2026, 11, 1);
            Assert.AreEqual(expectedDate, (await db.Jobs.SingleAsync()).ExpectedDailyFrom);
            Assert.AreEqual(1, await db.ReportDeliveries.CountAsync());
            var oldIds = await db.JobRuns.Select(x => x.Id).ToListAsync();
            Assert.AreEqual(1, await db.JobRuns.Where(x => oldIds.Contains(x.Id)).ExecuteDeleteAsync());
            Assert.AreEqual(1, await db.ReportDeliveries.Where(x => x.CustomerId == customer.Id).ExecuteDeleteAsync());
        }
        finally
        {
            await connection.ChangeDatabaseAsync("mysql");
            MySqlConnection.ClearAllPools();
            await new MySqlCommand($"DROP DATABASE `{name}`", connection).ExecuteNonQueryAsync();
        }
    }
}
