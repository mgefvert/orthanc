using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using Orthanc.Core.Entities;

namespace Orthanc.Core.Services;

public sealed record ReportEntry(
    string Name,
    string State,
    string Excerpt);

public sealed record ReportContent(
    string Subject,
    string Text,
    string Html);

public static class ReportService
{
    public static ReportContent Daily(DbCustomer customer, DateOnly date, DateTime deadline,
        IReadOnlyList<DbJob> jobs, IReadOnlyList<DbJobRun> runs)
    {
        var entries = new List<ReportEntry>();
        foreach (var job in jobs.Where(x => x.Enabled))
        {
            var matching = runs.Where(x => x.JobId == job.Id && x.ReceivedZ <= deadline).ToList();
            var name     = $"{job.MachineName}/{job.JobName}";
            if (matching.Count == 0 && job.ExpectedDailyFrom is { } first && first <= date)
                entries.Add(new(name, "Missing", "No start submission received before the report deadline."));

            foreach (var run in matching)
            {
                var completed = run.FinishedZ is { } finish && finish <= deadline;
                entries.Add(new($"{name} ({run.StartedZ:HH:mm:ss} UTC)",
                    completed ? run.Outcome.ToString() : "Incomplete",
                    completed ? Excerpt(run) : "Start observed; no completion before the report deadline."));
            }
        }

        return Render($"Orthanc — {customer.Name} — {date:yyyy-MM-dd}",
            $"Reporting day: {date:yyyy-MM-dd} ({customer.TimeZone}); completion deadline {deadline:O}. " +
            "Missing means no submission was received, not proof that the job did not run.", entries);
    }

    public static ReportContent Hourly(DbCustomer customer, DateTime start, DateTime end, IReadOnlyList<DbJobRun> runs)
    {
        return Render($"Orthanc exceptions — {customer.Name} — {start:yyyy-MM-dd HH:mm} UTC",
            $"Completions received {start:O} to {end:O} (exclusive).",
            runs.Select(x => new ReportEntry($"{x.Job.MachineName}/{x.Job.JobName}", x.Outcome.ToString(), Excerpt(x))).ToList());
    }

    private static string Excerpt(DbJobRun run)
    {
        var details =
            $"Exit code: {run.ExitCode?.ToString() ?? "unknown"}; duration: {run.DurationSeconds?.ToString("F1", CultureInfo.InvariantCulture) ?? "unknown"} seconds.";

        if (run.TerminationReason != null)
            details += " " + run.TerminationReason;
        if (run.Truncated || run.ClassificationLimited)
            details += " [Output or classification limited]";

        return details + "\n" + (run.ErrorText.Length > 0 ? run.ErrorText : run.WarningText);
    }

    private static int Rank(string state) => state switch
    {
        "Missing" => 0,
        "Incomplete" => 1,
        "Fatal" => 2,
        "Failed" => 3,
        "Warning" => 4,
        "Unknown" => 5,
        _ => 6
    };

    private static ReportContent Render(string subject, string introduction, List<ReportEntry> entries)
    {
        var counts = string.Join(", ", new[] { "Success", "Warning", "Failed", "Fatal", "Missing", "Incomplete", "Unknown" }
            .Select(state => $"{state}: {entries.Count(x => x.State == state)}"));

        var text = new StringBuilder()
            .AppendLine(subject)
            .AppendLine(introduction)
            .AppendLine(counts)
            .AppendLine();

        var html = new StringBuilder("<!doctype html><html><body>")
            .Append("<h1>")
            .Append(WebUtility.HtmlEncode(subject))
            .Append("</h1><p>")
            .Append(WebUtility.HtmlEncode(introduction))
            .Append("</p><p>")
            .Append(WebUtility.HtmlEncode(counts))
            .Append("</p>");

        var exceptions = entries
            .Where(x => x.State != "Success")
            .OrderBy(x => Rank(x.State))
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var entry in exceptions.Take(100))
        {
            var excerpt = entry.Excerpt.Length > 1600 ? entry.Excerpt[..1600] + "\n[Excerpt shortened]" : entry.Excerpt;

            text
                .AppendLine($"{entry.State}: {entry.Name}")
                .AppendLine(excerpt)
                .AppendLine();

            html
                .Append("<h2>")
                .Append(WebUtility.HtmlEncode($"{entry.State}: {entry.Name}"))
                .Append("</h2><pre style=\"white-space:pre-wrap\">")
                .Append(WebUtility.HtmlEncode(excerpt))
                .Append("</pre>");
        }

        if (exceptions.Count > 100)
        {
            text
                .AppendLine($"{exceptions.Count - 100} additional exceptions omitted from email; counts include them.");
            html
                .Append("<p>")
                .Append(exceptions.Count - 100)
                .Append(" additional exceptions omitted from email; counts include them.</p>");
        }

        var success = $"Successful runs: {entries.Count(x => x.State == "Success")}.";
        text
            .AppendLine(success);
        html
            .Append("<p>")
            .Append(success)
            .Append("</p></body></html>");

        return new ReportContent(subject, text.ToString(), html.ToString());
    }

    public static MailMessage Message(DbCustomer customer, string from, ReportContent report)
    {
        var message = new MailMessage
        {
            From            = new MailAddress(from),
            Subject         = report.Subject,
            Body            = report.Text,
            BodyEncoding    = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };

        var recipients = customer.ReportRecipients
            .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var recipient in recipients)
            message.To.Add(new MailAddress(recipient));

        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(report.Text, Encoding.UTF8, "text/plain"));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(report.Html, Encoding.UTF8, "text/html"));

        return message;
    }

    // Avoid loading full original logs for report generation; excerpts are bounded in SQL.
    public static IQueryable<DbJobRun> ForReport(this IQueryable<DbJobRun> query) => query.Select(x => new DbJobRun
    {
        Id                    = x.Id,
        JobId                 = x.JobId,
        Job                   = x.Job,
        StartedZ              = x.StartedZ,
        ReceivedZ             = x.ReceivedZ,
        FinishedZ             = x.FinishedZ,
        Outcome               = x.Outcome,
        Truncated             = x.Truncated,
        ExitCode              = x.ExitCode,
        DurationSeconds       = x.DurationSeconds,
        TerminationReason     = x.TerminationReason,
        ClassificationLimited = x.ClassificationLimited,
        ErrorCount            = x.ErrorCount,
        FatalCount            = x.FatalCount,
        ErrorText             = x.ErrorText.Substring(0, Math.Min(x.ErrorText.Length, 2000)),
        WarningText           = x.WarningText.Substring(0, Math.Min(x.WarningText.Length, 2000))
    });
}