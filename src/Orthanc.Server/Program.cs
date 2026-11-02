using System.Net;
using System.Threading.RateLimiting;
using DotNetCommons.Logging;
using DotNetCommons.Services;
using DotNetCommons.Services.Email;
using DotNetCommons.Web.Middleware;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Orthanc.Core;
using Orthanc.Core.Services;
using Orthanc.Server;
using Orthanc.Server.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddCommonConsole();

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8 * 1024 * 1024);

builder.Services.Configure<CommonLoggingOptions>(o => { o.UseUtcTimestamp = true; o.IncludeScopes = true; });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<OrthancContext>(o => o.UseMySQL(builder.Configuration.GetConnectionString("Orthanc")
                                                              ?? throw new InvalidOperationException("ConnectionStrings:Orthanc is required")));
builder.Services.AddScoped<ClassifierService>();
builder.Services.AddScoped<IngestionService>();
builder.Services.Configure<IntegrationConfiguration>(builder.Configuration.GetSection("Integrations"));
builder.Services.AddHostedService<ReportWorker>();

// Debug mode is opt-in: a missing SMTP configuration must never silently discard production reports.
if (builder.Configuration.GetValue("Reports:DebugEmail", false))
    builder.Services.AddScoped<IEmailIntegration, DebugEmailIntegration>();
else
    builder.Services.AddScoped<IEmailIntegration, SmtpClientIntegration>();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var ip in builder.Configuration.GetSection("TrustedProxies").Get<string[]>() ?? [])
        o.KnownProxies.Add(IPAddress.Parse(ip));
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetTokenBucketLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new()
        {
            TokenLimit = 240,
            TokensPerPeriod = 120,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 0
        }));
});

var app = builder.Build();

if (builder.Configuration.GetSection("TrustedProxies").Get<string[]>() is { Length: > 0 })
    app.UseForwardedHeaders();

app.UseMiddleware<AppExceptionMiddleware>();
app.UseWhen(c => c.Connection.RemoteIpAddress != null, branch => branch.UseMiddleware<IpBanMiddleware>());
app.UseRateLimiter();
app.UseMiddleware<AuthenticationPskMiddleware>();

app.Run();
