namespace Recoletas.Booking.Contracts.Models;

public sealed record RegisterPatientRequest(string Name, string? Nhc = null);
