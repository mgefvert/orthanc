namespace Orthanc.Core.Entities;

public sealed class DbCustomer
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string TimeZone { get; set; } = "Etc/UTC";
    public string ReportRecipients { get; set; } = "";
    public TimeSpan ReportTime { get; set; } = TimeSpan.FromMinutes(5);
    public bool Enabled { get; set; } = true;
    public bool HourlyAlerts { get; set; } = true;
    public DateTime CreatedZ { get; set; } = DateTime.UtcNow;
}