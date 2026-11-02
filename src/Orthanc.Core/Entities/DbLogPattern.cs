namespace Orthanc.Core.Entities;

public sealed class DbLogPattern
{
    public long Id { get; set; }
    public long? CustomerId { get; set; }
    public long? JobId { get; set; }
    public string Regex { get; set; } = "";
    public Severity Severity { get; set; }
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
}