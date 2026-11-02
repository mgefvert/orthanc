using DotNetCommons.Commands;
using DotNetCommons.Services.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orthanc.Core;
using Orthanc.Core.Services;

namespace Orthanc.Admin.Commands;

[CommandAction(["report", "test-send"], "Send a daily report to configured recipients; does not alter automatic delivery state", [])]
public sealed class ReportTestSend : ReportCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await using var db = OpenContext();

        var result = await Build(db, ct);
        var config = JobScope.GetRequiredService<IConfiguration>();

        using var message = ReportService.Message(result.Customer, config["Reports:From"] ?? throw new ArgumentException("Reports:From required"),
            result.Report);

        var sent = await JobScope.GetRequiredService<IEmailIntegration>().SendAsync([message], cancellationToken: ct);

        return sent.Count == 1 && sent[0].Result == Result.Success ? 0 : 1;
    }
}