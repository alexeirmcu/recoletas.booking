using FluentValidation;
using Recoletas.Booking.ApplicationServices.Errors;

namespace Recoletas.Booking.API.Middleware;

public sealed class BookingConflictException(string message) : Exception(message);

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ValidationException exception)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = BookingErrorCodes.ValidationFailed,
                details = exception.Errors.Select(failure => new
                {
                    field = failure.PropertyName,
                    code = failure.ErrorCode,
                    message = failure.ErrorMessage
                })
            });
        }
        catch (KeyNotFoundException exception)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = BookingErrorCodes.NotFound, message = exception.Message });
        }
        catch (PatientNhcAlreadyExistsException exception)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                error = BookingErrorCodes.PatientNhcAlreadyExists,
                message = exception.Message
            });
        }
        catch (BookingConflictException exception)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = BookingErrorCodes.Conflict, message = exception.Message });
        }
        catch (UnauthorizedAccessException exception)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = BookingErrorCodes.Unauthorized, message = exception.Message });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled request error.");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = BookingErrorCodes.InternalServerError });
        }
    }
}
