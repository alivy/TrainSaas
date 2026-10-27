using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace TrainSaas.Api.Errors;

/// <summary>
/// Maps exceptions to HTTP error responses as ProblemDetails JSON.
///
/// Domain exceptions carry user-visible text and map to client errors:
/// <see cref="NotFoundException"/>→404, <see cref="ConflictException"/>→409,
/// <see cref="ForbiddenActionException"/>→403, <see cref="NoAvailablePackException"/>→400,
/// <see cref="BusinessRuleException"/>→400 — their message becomes ProblemDetails Detail.
///
/// Every other exception — including bare <see cref="InvalidOperationException"/>
/// thrown by LINQ/EF Core/ASP.NET infrastructure — maps to 500 with a generic
/// detail; the original exception (message + stack trace) is logged via
/// <see cref="ILogger{TCategoryName}"/> and never sent to the client.
/// </summary>
public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    private const string InternalErrorDetail = "服务器内部错误";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            var (statusCode, detail) = ex switch
            {
                NotFoundException => (StatusCodes.Status404NotFound, ex.Message),
                ConflictException => (StatusCodes.Status409Conflict, ex.Message),
                ForbiddenActionException => (StatusCodes.Status403Forbidden, ex.Message),
                NoAvailablePackException => (StatusCodes.Status400BadRequest, ex.Message),
                BusinessRuleException => (StatusCodes.Status400BadRequest, ex.Message),
                _ => (StatusCodes.Status500InternalServerError, InternalErrorDetail),
            };

            if (statusCode == StatusCodes.Status500InternalServerError)
            {
                logger.LogError(
                    ex,
                    "Unhandled exception while processing {Method} {Path}",
                    context.Request.Method,
                    context.Request.Path);
            }

            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = statusCode,
                    Title = ReasonPhrases.GetReasonPhrase(statusCode),
                    Detail = detail,
                },
                options: null,
                contentType: "application/problem+json");
        }
    }
}
