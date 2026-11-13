using System.Net;
using Recoletas.Booking.API.Auth;
using Recoletas.Booking.ApplicationServices.Errors;

namespace Recoletas.Booking.API.Middleware;

public sealed class PatientIdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var isSwagger = context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase);
        var isPatientRegistration = HttpMethods.IsPost(context.Request.Method) &&
                                    string.Equals(context.Request.Path.Value, "/patients",
                                        StringComparison.OrdinalIgnoreCase);
        var providedPatientId = context.Request.Headers.TryGetValue("X-Patient-Id", out var headerValue);

        if (providedPatientId)
        {
            if (!Guid.TryParse(headerValue.ToString(), out var patientId) || patientId == Guid.Empty)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = BookingErrorCodes.InvalidPatientIdentity });
                return;
            }

            context.Items[PatientIdentityResolver.PatientIdItemKey] = patientId;
        }
        else if (!isSwagger && !isPatientRegistration)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = BookingErrorCodes.PatientIdentityRequired });
            return;
        }

        await next(context);
    }
}
