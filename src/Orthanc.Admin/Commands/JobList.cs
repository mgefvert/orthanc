using DotNetCommons.Commands;
using Microsoft.EntityFrameworkCore;

namespace Orthanc.Admin.Commands;

[CommandAction(["job", "list"], "List jobs; enable expectations manually in SQL", [])]
public sealed class JobList : AdminCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await using var db = OpenContext(); await GetArgsCustomer(db, ct);

        var jobs = await db.Jobs
            .Where(x => x.CustomerId == Args.Customer)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        foreach (var job in jobs)
            Console.WriteLine(string.Join("\t", job.Id, job.MachineName, job.JobName,
                $"expected-from={job.ExpectedDailyFrom:yyyy-MM-dd}", $"enabled={job.Enabled}"));

        return 0;
    }
}