using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace TrainSaas.Api.Errors;

/// <summary>
/// Maps service-layer exceptions to HTTP error responses (ProblemDetails JSON
/// carrying the exception message): <see cref="NotFoundException"/>→404,
/// <see cref="ConflictException"/>→409, <see cref="ForbiddenActionException"/>→403,
/// <see cref="NoAvailablePackException"/>→400, <see cref="InvalidOperationException"/>→400.
/// Any other exception is left to the default pipeline (HTTP 500).
/// </summary>
public class ExceptionHandlingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (ex is NotFoundException
                                      or ConflictException
                                      or ForbiddenActionException
                                      or NoAvailablePackException
                                      or InvalidOperationException)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            var statusCode = ex switch
            {
                NotFoundException => StatusCodes.Status404NotFound,
                ConflictException => StatusCodes.Status409Conflict,
                ForbiddenActionException => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status400BadRequest,
            };

            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = statusCode,
                    Title = ReasonPhrases.GetReasonPhrase(statusCode),
                    Detail = ex.Message,
                },
                options: null,
                contentType: "application/problem+json");
        }
    }
}
