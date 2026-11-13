namespace Recoletas.Booking.Contracts.Models;

public sealed record PatientSummary(Guid PatientId, string Name, string? Nhc, DateTimeOffset CreatedAtUtc);
