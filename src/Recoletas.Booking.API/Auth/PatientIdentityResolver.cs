namespace Recoletas.Booking.API.Auth;

public sealed class PatientIdentityResolver(IHttpContextAccessor httpContextAccessor) : IPatientIdentityResolver
{
    public Guid GetRequiredPatientId()
    {
        var claimValue = httpContextAccessor.HttpContext?.User
            .FindFirst(BookingClaimTypes.PatientId)?.Value;
        if (Guid.TryParse(claimValue, out var patientId) && patientId != Guid.Empty)
        {
            return patientId;
        }

        throw new UnauthorizedAccessException("A valid patientId claim is required.");
    }
}
