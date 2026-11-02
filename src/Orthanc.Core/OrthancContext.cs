using DotNetCommons.EF.EfCore;
using Microsoft.EntityFrameworkCore;
using Orthanc.Core.Entities;

namespace Orthanc.Core;

public sealed class OrthancContext(DbContextOptions<OrthancContext> options) : DbContext(options)
{
    public DbSet<DbCustomer> Customers => Set<DbCustomer>();
    public DbSet<DbApiKey> ApiKeys => Set<DbApiKey>();
    public DbSet<DbJob> Jobs => Set<DbJob>();
    public DbSet<DbJobRun> JobRuns => Set<DbJobRun>();
    public DbSet<DbLogPattern> LogPatterns => Set<DbLogPattern>();
    public DbSet<DbReportDelivery> ReportDeliveries => Set<DbReportDelivery>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DbCustomer>(e =>
        {
            e.ToTable("Customer");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.TimeZone).HasMaxLength(100);
            e.Property(x => x.ReportRecipients).HasMaxLength(2000);
        });

        b.Entity<DbApiKey>(e =>
        {
            e.ToTable("ApiKey");
            e.Property(x => x.KeyHash).HasMaxLength(64);
            e.Property(x => x.Label).HasMaxLength(200);
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasOne<DbCustomer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DbJob>(e =>
        {
            e.ToTable("Job");
            e.Property(x => x.MachineName).HasMaxLength(200);
            e.Property(x => x.JobName).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.ExpectedDailyFrom).HasConversion<EfDateOnlyConverter>().HasColumnType("date");
            e.HasIndex(x => new { x.CustomerId, x.MachineName, x.JobName }).IsUnique();
            e.HasOne<DbCustomer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DbJobRun>(e =>
        {
            e.ToTable("JobRun");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SourceIp).HasMaxLength(45);
            e.Property(x => x.ProtocolVersion).HasMaxLength(20);
            e.Property(x => x.Mode).HasMaxLength(20);
            e.Property(x => x.TerminationReason).HasMaxLength(200);
            e.Property(x => x.CompletionHash).HasMaxLength(64);
            e.Property(x => x.CompletionHash).IsConcurrencyToken();
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.JobId, x.StartedZ });
            e.HasIndex(x => x.CompletedReceivedZ);
            e.HasIndex(x => x.Outcome);

            foreach (var name in new[] { nameof(DbJobRun.LogText), nameof(DbJobRun.WarningText), nameof(DbJobRun.ErrorText) })
                e.Property<string>(name).HasColumnType("longtext");
        });

        b.Entity<DbLogPattern>(e =>
        {
            e.ToTable("LogPattern");
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Regex).HasMaxLength(2000);
            e.HasOne<DbCustomer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DbJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DbReportDelivery>(e =>
        {
            e.ToTable("ReportDelivery");
            e.Property(x => x.Kind).HasMaxLength(20);
            e.HasIndex(x => new { x.CustomerId, x.Kind, x.PeriodStartZ }).IsUnique();
            e.HasOne<DbCustomer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var p in entity.GetProperties())
        {
            if (p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))
                p.SetColumnType("datetime(3)");
        }
    }
}