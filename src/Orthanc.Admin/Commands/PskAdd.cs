using DotNetCommons.Commands;

namespace Orthanc.Admin.Commands;

[CommandAction(["psk", "add"], "Generate a PSK; secret displayed once", [])]
public sealed class PskAdd : AdminCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await using var db = OpenContext();

        var c = await GetArgsCustomer(db, ct);
        await AddKey(db, c.Id, ct);

        return 0;
    }
}