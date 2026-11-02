using DotNetCommons.Sys;

namespace Orthanc.Admin;

public class AdminArgs
{
    [CommandLineOption('c', "connection", "Required MySQL login-path (local, beta, production, etc.)")]
    public string Connection { get; set; } = "";

    [CommandLineOption('d', "database", "Database name, default orthanc")]
    public string Database { get; set; } = "orthanc";

    [CommandLineOption("login-file", "Explicit .mylogin.cnf path")]
    public string? LoginFile { get; set; }

    [CommandLineOption("customer", "Customer ID")]
    public long Customer { get; set; }

    [CommandLineOption("name", "Customer name")]
    public string Name { get; set; } = "";

    [CommandLineOption("timezone", "IANA timezone, default Etc/UTC")]
    public string TimeZone { get; set; } = "Etc/UTC";

    [CommandLineOption("recipients", "Comma-separated report recipients")]
    public string Recipients { get; set; } = "";

    [CommandLineOption("report-time", "Customer-local report time HH:mm, default 00:05")]
    public string ReportTime { get; set; } = "00:05";

    [CommandLineOption("label", "PSK description")]
    public string Label { get; set; } = "collector";

    [CommandLineOption("key", "PSK record ID to revoke (not the secret)")]
    public long Key { get; set; }

    [CommandLineOption("date", "Customer-local report day yyyy-MM-dd; default yesterday")]
    public string? Date { get; set; }
}