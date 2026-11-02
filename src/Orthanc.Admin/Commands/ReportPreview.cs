using DotNetCommons.Commands;

namespace Orthanc.Admin.Commands;

[CommandAction(["report", "preview"], "Print the plain-text daily report without sending", [])]
public sealed class ReportPreview : ReportCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await using var db = OpenContext();

        var result = await Build(db, ct);
        Console.WriteLine(result.Report.Text);

        return 0;
    }
}