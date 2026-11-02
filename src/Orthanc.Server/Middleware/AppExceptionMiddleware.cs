using Orthanc.Core;
using Orthanc.Core.Services;

namespace Orthanc.Server.Middleware;

public class AppExceptionMiddleware
{
    private readonly ILogger<AuthenticationPskMiddleware> _logger;

    public AppExceptionMiddleware(ILogger<AuthenticationPskMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex)
        {
            await Results.Problem(statusCode: ex.StatusCode, title: "Invalid request body").ExecuteAsync(context);
        }
        catch (ProtocolException ex)
        {
            await Results.Problem(statusCode: ex.Status, title: ex.Message).ExecuteAsync(context);
        }
        catch (ArgumentException)
        {
            await Results.Problem(statusCode: 400, title: "Invalid request values").ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request failed: {Method} {Path}", context.Request.Method, context.Request.Path);
            await Results.Problem(statusCode: 500, title: "Internal server error").ExecuteAsync(context);
        }
    }
}