using DotNetCommons.Commands;

namespace Orthanc.Admin.Commands;

[CommandAction(["customer", "update"], "Set customer name, timezone, recipients and report time", [])]
public sealed class CustomerUpdate : AdminCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await using var db = OpenContext();

        var c = await GetArgsCustomer(db, ct);
        ConfigureCustomerFromArgs(c);
        await db.SaveChangesAsync(ct);

        return 0;
    }
}