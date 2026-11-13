namespace Recoletas.Booking.Domain.Exceptions;

public sealed class DuplicatePatientNhcException()
    : Exception("A patient with this NHC already exists.");
