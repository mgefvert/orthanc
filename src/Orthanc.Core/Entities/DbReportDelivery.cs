namespace Orthanc.Core.Entities;

public sealed class DbReportDelivery
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public string Kind { get; set; } = "daily";
    public DateTime PeriodStartZ { get; set; }
    public DateTime PeriodEndZ { get; set; }
    public DateTime? SentZ { get; set; }
    public DateTime? LastAttemptZ { get; set; }
    public int Attempts { get; set; }
}