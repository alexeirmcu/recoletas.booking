namespace Recoletas.Booking.API.Auth;

public sealed class PatientIdentityResolver(IHttpContextAccessor httpContextAccessor) : IPatientIdentityResolver
{
    internal const string PatientIdItemKey = "Recoletas.Booking.PatientId";

    public Guid GetRequiredPatientId()
    {
        var context = httpContextAccessor.HttpContext;
        if (context?.Items[PatientIdItemKey] is Guid patientId)
        {
            return patientId;
        }

        throw new UnauthorizedAccessException("A valid X-Patient-Id header is required.");
    }
}
