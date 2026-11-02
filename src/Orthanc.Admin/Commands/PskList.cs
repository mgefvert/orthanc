using DotNetCommons.Commands;
using Microsoft.EntityFrameworkCore;

namespace Orthanc.Admin.Commands;

[CommandAction(["psk", "list"], "List key metadata for a customer; never display verifiers", [])]
public sealed class PskList : AdminCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await using var db = OpenContext();

        await GetArgsCustomer(db, ct);

        var keys = await db.ApiKeys
            .Where(x => x.CustomerId == Args.Customer)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        foreach (var key in keys)
            Console.WriteLine(string.Join("\t", key.Id, key.Label, key.CreatedZ.ToString("s"), $"revoked={key.RevokedZ:s}"));

        return 0;
    }
}