namespace Recoletas.Booking.ApplicationServices.Errors;

public sealed class DuplicatePatientNhcException()
    : Exception("A patient with this NHC already exists.");
