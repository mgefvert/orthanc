using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Orthanc.Core.Entities;

namespace Orthanc.Core.Services;

public sealed record Classification(
    Outcome Outcome,
    int WarningCount,
    int ErrorCount,
    int FatalCount,
    string WarningText,
    string ErrorText,
    bool Limited);

public sealed class ClassifierService(ILogger<ClassifierService> logger)
{
    private static readonly DbLogPattern[] Defaults =
    [
        new() { Regex = @"\b(panic|fatal)\b", Severity = Severity.Fatal,   Priority = 0 },
        new() { Regex = @"\berrors?\b",       Severity = Severity.Error,   Priority = 1 },
        new() { Regex = @"\bwarn(ing)?s?\b",  Severity = Severity.Warning, Priority = 2 }
    ];

    public Classification Classify(CompleteRequest request, IEnumerable<DbLogPattern> patterns)
    {
        var limited = false;
        var rules = new List<(Regex Regex, Severity Severity)>();

        var orderedPatterns = patterns
            .Where(x => x.Enabled)
            .OrderBy(x => x.JobId.HasValue ? 0 : x.CustomerId.HasValue ? 1 : 2)
            .ThenBy(x => x.Priority)
            .ThenBy(x => x.Id)
            .Concat(Defaults)
            .Take(101);

        foreach (var p in orderedPatterns)
        {
            if (rules.Count == 100)
            {
                limited = true;
                break;
            }

            try
            {
                rules.Add(
                    (
                        new Regex(p.Regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25)),
                        p.Severity
                    ));
            }
            catch (ArgumentException)
            {
                limited = true;
                logger.LogWarning("Invalid classification pattern {PatternId}; skipped", p.Id);
            }
        }

        int warnings     = 0,
            errors       = 0,
            fatals       = 0;
        var warningLines = new List<string>();
        var errorLines   = new List<string>();
        var watch        = Stopwatch.StartNew();

        foreach (var line in request.Lines)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(2))
            {
                limited = true;
                break;
            }

            foreach (var (regex, severity) in rules)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(2))
                {
                    limited = true;
                    break;
                }

                bool matches;
                try
                {
                    matches = regex.IsMatch(line.Text);
                }
                catch (RegexMatchTimeoutException)
                {
                    limited = true;
                    continue;
                }

                if (!matches)
                    continue;

                switch (severity)
                {
                    case Severity.Warning: warnings++; warningLines.Add(IngestionService.FormatLine(line)); break;
                    case Severity.Error:   errors++;   errorLines.Add(IngestionService.FormatLine(line));   break;
                    case Severity.Fatal:   fatals++;   errorLines.Add(IngestionService.FormatLine(line));   break;
                }
                break;
            }
        }

        var outcome = fatals > 0 ? Outcome.Fatal
            : errors > 0 || request.TimedOut || request.TerminationReason != null ||
              (request.ExitCode.HasValue && request.ExitCode != 0 && !request.IgnoreExitCode) ? Outcome.Failed
            : warnings > 0 ? Outcome.Warning
            : request.ExitCode == null || limited || request.Truncated ? Outcome.Unknown
            : Outcome.Success;

        return new Classification(outcome, warnings, errors, fatals,
            string.Join('\n', warningLines), string.Join('\n', errorLines),
            limited);
    }
}
