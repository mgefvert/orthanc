using Microsoft.EntityFrameworkCore;
using Orthanc.Core;

namespace Orthanc.Server;

public class AuthenticationPskMiddleware
{
    private readonly ILogger<AuthenticationPskMiddleware> _logger;

    public AuthenticationPskMiddleware(ILogger<AuthenticationPskMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            if (!context.Request.IsHttps)
            {
                await Results.Problem(statusCode: 400, title: "HTTPS required").ExecuteAsync(context);
                return;
            }

            var header = context.Request.Headers.Authorization.ToString();
            var token  = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
            if (token.Length != 64 || !token.All(char.IsAsciiHexDigit))
            {
                context.Response.StatusCode = 401;
                return;
            }

            var db   = context.RequestServices.GetRequiredService<OrthancContext>();
            var hash = Credentials.Hash(token);

            var customer = await (
                from key in db.ApiKeys.AsNoTracking()
                join c in db.Customers.AsNoTracking() on key.CustomerId equals c.Id
                where
                    key.KeyHash == hash &&
                    key.RevokedZ == null &&
                    c.Enabled
                select (long?)c.Id
            ).SingleOrDefaultAsync(context.RequestAborted);

            if (customer == null)
            {
                _logger.LogWarning("Rejected collector credential from {SourceIp}", context.Connection.RemoteIpAddress);
                context.Response.StatusCode = 401;
                return;
            }

            context.Items["CustomerId"] = customer.Value;
        }

        await next(context);
    }
}