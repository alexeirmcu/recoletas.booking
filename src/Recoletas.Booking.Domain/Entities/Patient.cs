using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Domain.Entities;

public sealed class Patient
{
    public Patient(PatientId id, string name, DateTimeOffset createdAtUtc, string? nhc = null,
        DateTimeOffset? deletedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A patient name is required.", nameof(name));
        }

        Id = id;
        Name = name;
        Nhc = string.IsNullOrWhiteSpace(nhc) ? null : nhc.Trim();
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        DeletedAtUtc = deletedAtUtc?.ToUniversalTime();
    }

    public PatientId Id { get; }

    public string Name { get; }

    public string? Nhc { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? DeletedAtUtc { get; private set; }
}
