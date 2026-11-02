using System.Globalization;
using DotNetCommons.Commands;
using DotNetCommons.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using Orthanc.Core;
using Orthanc.Core.Entities;
using Orthanc.Core.Services;

namespace Orthanc.Admin.Commands;

public abstract class AdminCommand : CommandAction<AdminArgs>
{
    protected OrthancContext OpenContext()
    {
        if (string.IsNullOrWhiteSpace(Args.Connection))
            throw new ArgumentException("--connection is required");

        var reader = JobScope.GetRequiredService<MySqlCnfReader>();
        var file = Args.LoginFile ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MySQL", ".mylogin.cnf")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mylogin.cnf"));

        if (!reader.Load(file))
            throw new ArgumentException("MySQL login file not found");

        // Read the selected login path through the shared helper, then escape connection values
        // with Oracle's builder (passwords may legitimately contain semicolons).
        _ = reader.RequireConnectionString(Args.Connection, Args.Database);
        var entry = reader.Entries[Args.Connection];
        var connection = new MySqlConnectionStringBuilder
        {
            Server   = entry.Host,
            UserID   = entry.User,
            Password = entry.Password,
            Database = Args.Database
        }.ConnectionString;

        return new OrthancContext(new DbContextOptionsBuilder<OrthancContext>().UseMySQL(connection).Options);
    }

    protected async Task<DbCustomer> GetArgsCustomer(OrthancContext context, CancellationToken ct)
    {
        return await context.Customers.SingleOrDefaultAsync(x => x.Id == Args.Customer, ct)
               ?? throw new ArgumentException("Customer not found");
    }

    protected DbCustomer ConfigureCustomerFromArgs(DbCustomer c)
    {
        if (string.IsNullOrWhiteSpace(Args.Name) || Args.Name.Length > 200 || Args.Recipients.Length > 2000)
            throw new ArgumentException("Customer name and valid recipients required");

        _ = TimeZoneInfo.FindSystemTimeZoneById(Args.TimeZone);
        if (!TimeOnly.TryParseExact(Args.ReportTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new ArgumentException("Report time must be HH:mm");

        // Reuse recipient validation without sending.
        using var message = ReportService.Message(new DbCustomer
        {
            ReportRecipients = Args.Recipients
        }, "validate@example.invalid", new("", "", ""));

        c.Name             = Args.Name.Trim();
        c.TimeZone         = Args.TimeZone;
        c.ReportRecipients = Args.Recipients;
        c.ReportTime       = time.ToTimeSpan();

        return c;
    }

    protected async Task AddKey(OrthancContext context, long customerId, CancellationToken ct)
    {
        if (Args.Label.Length > 200)
            throw new ArgumentException("Label too long");

        var secret = Credentials.Generate();
        var key    = new DbApiKey
        {
            CustomerId = customerId,
            Label = Args.Label,
            KeyHash = Credentials.Hash(secret),
            CreatedZ = DateTime.UtcNow
        };

        context.ApiKeys.Add(key);
        await context.SaveChangesAsync(ct);

        Console.WriteLine($"Key ID: {key.Id}\nPSK (shown once): {secret}");
    }
}