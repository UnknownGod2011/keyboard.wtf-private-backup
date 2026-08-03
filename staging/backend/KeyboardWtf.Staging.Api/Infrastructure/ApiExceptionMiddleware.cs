using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace KeyboardWtf.Staging.Api.Infrastructure;

public sealed class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiExceptionMiddleware> _logger;

    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 499;
        }
        catch (Exception exception)
        {
            var (status, title) = exception switch
            {
                ArgumentException => (HttpStatusCode.BadRequest, "Invalid request"),
                KeyNotFoundException => (HttpStatusCode.NotFound, "Not found"),
                AuthenticationRequiredException => (HttpStatusCode.Unauthorized, "Sign-in required"),
                UnauthorizedAccessException => (HttpStatusCode.Forbidden, "Access denied"),
                InvalidOperationException when exception.Message.Contains("credits", StringComparison.OrdinalIgnoreCase)
                    => ((HttpStatusCode)402, "Credits required"),
                InvalidOperationException => (HttpStatusCode.Conflict, "Request could not be completed"),
                _ => (HttpStatusCode.InternalServerError, "Unexpected server error"),
            };
            if ((int)status >= 500)
                _logger.LogError(exception, "Unhandled API failure for {Method} {Path}", context.Request.Method, context.Request.Path);
            else
                _logger.LogWarning("API request rejected: {Status} {Title} ({Method} {Path})", (int)status, title, context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                return;
            context.Response.StatusCode = (int)status;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = (int)status,
                Title = title,
                Detail = (int)status >= 500 ? "The service could not complete this request." : exception.Message,
                Instance = context.Request.Path,
            });
        }
    }
}

public sealed class AuthenticationRequiredException : Exception
{
    public AuthenticationRequiredException(string message) : base(message) { }
}
