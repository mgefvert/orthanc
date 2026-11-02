namespace Orthanc.Core.Entities;

public sealed class DbJob
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public string MachineName { get; set; } = "";
    public string JobName { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    // NULL means unmonitored. Administrator sets a local date to enable daily expectations.
    public DateOnly? ExpectedDailyFrom { get; set; }
    public DateTime CreatedZ { get; set; }
}