using DotNetCommons.Commands;
using Microsoft.EntityFrameworkCore;

namespace Orthanc.Admin.Commands;

[CommandAction(["psk", "revoke"], "Revoke a key by --customer and --key ID", [])]
public sealed class PskRevoke : AdminCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await using var db = OpenContext();

        var key = await db.ApiKeys.SingleOrDefaultAsync(x => x.Id == Args.Key && x.CustomerId == Args.Customer, ct)
                  ?? throw new ArgumentException("Key not found");

        key.RevokedZ ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return 0;
    }
}