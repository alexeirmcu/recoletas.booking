namespace Recoletas.Booking.API.Auth;

public interface IPatientIdentityResolver
{
    Guid GetRequiredPatientId();
}
