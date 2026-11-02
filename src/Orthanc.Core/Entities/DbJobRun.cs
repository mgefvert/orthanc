namespace Orthanc.Core.Entities;

public sealed class DbJobRun
{
    public Guid Id { get; set; }
    public long JobId { get; set; }
    public DbJob Job { get; set; } = null!;
    public DateTime StartedZ { get; set; }
    public DateTime ReceivedZ { get; set; }
    public DateTime? FinishedZ { get; set; }
    public DateTime? CompletedReceivedZ { get; set; }
    public double? DurationSeconds { get; set; }
    public int? ExitCode { get; set; }
    public Outcome Outcome { get; set; }
    public string SourceIp { get; set; } = "";
    public string ProtocolVersion { get; set; } = "1";
    public string Mode { get; set; } = "command";
    public bool LateRegistration { get; set; }
    public bool IgnoreExitCode { get; set; }
    public bool TimedOut { get; set; }
    public string? TerminationReason { get; set; }
    public bool Truncated { get; set; }
    public long DroppedBytes { get; set; }
    public string LogText { get; set; } = "";
    public string WarningText { get; set; } = "";
    public string ErrorText { get; set; } = "";
    public int WarningCount { get; set; }
    public int ErrorCount { get; set; }
    public int FatalCount { get; set; }
    public bool ClassificationLimited { get; set; }
    public string? CompletionHash { get; set; }
}