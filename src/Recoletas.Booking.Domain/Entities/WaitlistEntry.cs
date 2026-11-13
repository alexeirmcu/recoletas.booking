using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Domain.Entities;

public sealed class WaitlistEntry
{
    public WaitlistEntry(Guid id, PatientId patientId, TestType testType, DateTimeOffset joinedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A waitlist entry identifier cannot be empty.", nameof(id));
        }

        Id = id;
        PatientId = patientId;
        TestType = testType ?? throw new ArgumentNullException(nameof(testType));
        JoinedAtUtc = joinedAtUtc.ToUniversalTime();
    }

    public Guid Id { get; }

    public PatientId PatientId { get; }

    public TestType TestType { get; }

    public DateTimeOffset JoinedAtUtc { get; }

    public bool IsActive { get; private set; } = true;

    public void Remove() => IsActive = false;
}
