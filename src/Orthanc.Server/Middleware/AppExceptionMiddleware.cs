using Orthanc.Core;

namespace Orthanc.Server.Middleware;

public class AppExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AppExceptionMiddleware> _logger;

    public AppExceptionMiddleware(RequestDelegate next, ILogger<AppExceptionMiddleware> logger)
    {
        _next   = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
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
