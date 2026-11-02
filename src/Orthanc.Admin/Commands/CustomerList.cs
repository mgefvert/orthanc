using DotNetCommons.Commands;
using Microsoft.EntityFrameworkCore;

namespace Orthanc.Admin.Commands;

[CommandAction(["customer", "list"], "List customers", [])]
public sealed class CustomerList : AdminCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await using var db = OpenContext();

        foreach (var c in await db.Customers.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct))
            Console.WriteLine(string.Join("\t", c.Id, c.Name, c.TimeZone, c.ReportTime, $"enabled={c.Enabled}"));

        return 0;
    }
}