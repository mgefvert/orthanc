using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orthanc.Core.Entities;

namespace Orthanc.Core.Services;

public sealed record StartRequest(
    string MachineName,
    string JobName,
    DateTimeOffset StartedZ,
    string ProtocolVersion = "1",
    string Mode = "command",
    bool LateRegistration = false);

public sealed record LogLine(
    string Stream,
    string Text,
    DateTimeOffset? TimestampZ = null);

public sealed record CompleteRequest(
    DateTimeOffset FinishedZ,
    double DurationSeconds,
    int? ExitCode,
    List<LogLine> Lines,
    bool IgnoreExitCode = false,
    bool TimedOut = false,
    string? TerminationReason = null,
    bool Truncated = false,
    long DroppedBytes = 0);

public sealed class IngestionService(OrthancContext context, TimeProvider clock, ClassifierService classifierService)
{
    public const int MaxLogBytes = 1024 * 1024;
    public const int MaxLines = 20000;
    public const int MaxLineChars = 16384;

    public async Task<DbJobRun> Start(long customerId, StartRequest request, string sourceIp, CancellationToken ct)
    {
        if (request.ProtocolVersion != "1")
            throw new ProtocolException(400, "Unsupported protocol version");

        if (request.Mode is not ("command" or "stdin"))
            throw new ProtocolException(400, "Invalid mode");

        var machine = Identity.Normalize(request.MachineName);
        var name    = Identity.Normalize(request.JobName);
        var now     = clock.GetUtcNow().UtcDateTime;
        var start   = request.StartedZ.UtcDateTime;
        if (start > now.AddMinutes(5) || start < now.AddDays(-30))
            throw new ProtocolException(400, "Start time must be within 30 days and no more than five minutes in the future");

        var job = await context.Jobs.SingleOrDefaultAsync(x => x.CustomerId == customerId && x.MachineName == machine && x.JobName == name, ct);
        if (job == null)
        {
            job = new DbJob
            {
                CustomerId = customerId,
                MachineName = machine,
                JobName = name,
                CreatedZ = now
            };
            context.Jobs.Add(job);
            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A simultaneous first submission may have registered the same natural key.
                context.Entry(job).State = EntityState.Detached;
                job = await context.Jobs.SingleOrDefaultAsync(x => x.CustomerId == customerId && x.MachineName == machine && x.JobName == name, ct);
                if (job == null)
                    throw;
            }
        }

        if (!job.Enabled)
            throw new ProtocolException(403, "Job is disabled");

        var run = new DbJobRun
        {
            Id = Guid.NewGuid(),
            Job = job,
            JobId = job.Id,
            StartedZ = start,
            ReceivedZ = now,
            SourceIp = sourceIp,
            ProtocolVersion = request.ProtocolVersion,
            Mode = request.Mode,
            LateRegistration = request.LateRegistration
        };

        context.JobRuns.Add(run);
        await context.SaveChangesAsync(ct);
        return run;
    }

    public async Task<DbJobRun> Complete(long customerId, Guid id, CompleteRequest request, CancellationToken ct)
    {
        var run = await context.JobRuns
                      .Include(x => x.Job)
                      .SingleOrDefaultAsync(x => x.Id == id && x.Job.CustomerId == customerId, ct)
            ?? throw new ProtocolException(404, "Run not found");

        Validate(request, run, clock.GetUtcNow().UtcDateTime);

        var hash = Credentials.Hash(JsonSerializer.Serialize(request));
        if (run.CompletionHash != null)
        {
            if (run.CompletionHash != hash)
                throw new ProtocolException(409, "Run already completed with different data");
            return run;
        }

        var patterns = await context.LogPatterns.AsNoTracking()
            .Where(x => x.Enabled && (x.CustomerId == null || x.CustomerId == customerId) && (x.JobId == null || x.JobId == run.JobId))
            .ToListAsync(ct);

        var classified = classifierService.Classify(request, patterns);

        run.FinishedZ             = request.FinishedZ.UtcDateTime;
        run.CompletedReceivedZ    = clock.GetUtcNow().UtcDateTime;
        run.DurationSeconds       = request.DurationSeconds;
        run.ExitCode              = request.ExitCode;
        run.IgnoreExitCode        = request.IgnoreExitCode;
        run.TimedOut              = request.TimedOut;
        run.TerminationReason     = request.TerminationReason;
        run.Truncated             = request.Truncated;
        run.DroppedBytes          = request.DroppedBytes;
        run.LogText               = string.Join('\n', request.Lines.Select(FormatLine));
        run.WarningText           = classified.WarningText;
        run.ErrorText             = classified.ErrorText;
        run.WarningCount          = classified.WarningCount;
        run.ErrorCount            = classified.ErrorCount;
        run.FatalCount            = classified.FatalCount;
        run.Outcome               = classified.Outcome;
        run.ClassificationLimited = classified.Limited;
        run.CompletionHash        = hash;

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await context.Entry(run).ReloadAsync(ct);
            if (run.CompletionHash != hash)
                throw new ProtocolException(409, "Run already completed with different data");
        }

        return run;
    }

    public static string FormatLine(LogLine line) =>
        $"{(line.TimestampZ is { } stamp ? stamp.UtcDateTime.ToString("O") + " " : "")}[{line.Stream}] {line.Text}";

    private static void Validate(CompleteRequest r, DbJobRun run, DateTime now)
    {
        if (r.FinishedZ.UtcDateTime < run.StartedZ
            || r.FinishedZ.UtcDateTime > now.AddMinutes(5)
            || !double.IsFinite(r.DurationSeconds)
            || r.DurationSeconds < 0
            || r.DroppedBytes < 0
            || r.TerminationReason?.Length > 200
            || r.Lines == null!
            || r.Lines.Count > MaxLines)
            throw new ProtocolException(400, "Invalid completion metadata or too many log lines");

        long size = 0;
        foreach (var line in r.Lines)
        {
            if (line == null || line.Text == null || line.Stream is not ("stdout" or "stderr" or "stdin") || line.Text.Length > MaxLineChars)
                throw new ProtocolException(400, "Invalid log line");

            size += Encoding.UTF8.GetByteCount(line.Text);
        }

        if (size > MaxLogBytes)
            throw new ProtocolException(413, "Log exceeds one MiB");
    }
}
