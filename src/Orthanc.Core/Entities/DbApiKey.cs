namespace Orthanc.Core.Entities;

public sealed class DbApiKey
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public string KeyHash { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime CreatedZ { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedZ { get; set; }
}