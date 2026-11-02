using DotNetCommons.Commands;
using DotNetCommons.Logging;
using DotNetCommons.Security;
using DotNetCommons.Services;
using DotNetCommons.Services.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

try
{
    var configuration = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddLogging(o => o.AddCommonConsole());
    services.Configure<CommonLoggingOptions>(o => o.UseUtcTimestamp = true);
    services.Configure<IntegrationConfiguration>(configuration.GetSection("Integrations"));
    services.AddTransient<MySqlCnfReader>();
    services.AddTransient<IEmailIntegration, SmtpClientIntegration>();

    await using var provider = services.BuildServiceProvider();
    return await new CommandActionRegistry(provider).RegisterThis().Execute(args);
}
catch (Exception ex)
{
    // Connection strings, database exceptions and SMTP errors can contain credentials.
    Console.Error.WriteLine($"orthanc-admin: {ex.Message}");
    return 1;
}
