namespace Recoletas.Booking.Contracts.Models;

public sealed record BookingResult(bool Succeeded, string? ErrorCode = null, int? Generation = null,
    AppointmentSummary? Appointment = null);
