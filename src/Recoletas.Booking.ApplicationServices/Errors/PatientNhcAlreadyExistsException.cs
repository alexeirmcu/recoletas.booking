namespace Recoletas.Booking.ApplicationServices.Errors;

public sealed class PatientNhcAlreadyExistsException()
    : Exception("A patient with this NHC already exists.");
