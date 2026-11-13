using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Domain.Entities;

public sealed class Slot
{
    private readonly List<object> _pendingEvents = [];

    public Slot(SlotId id, TestType testType, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("A slot must end after it starts.", nameof(endsAtUtc));
        }

        Id = id;
        TestType = testType ?? throw new ArgumentNullException(nameof(testType));
        StartsAtUtc = startsAtUtc.ToUniversalTime();
        EndsAtUtc = endsAtUtc.ToUniversalTime();
        State = SlotState.Available;
    }

    public SlotId Id { get; }

    public TestType TestType { get; }

    public DateTimeOffset StartsAtUtc { get; }

    public DateTimeOffset EndsAtUtc { get; }

    public SlotState State { get; private set; }

    public PatientId? AssignedPatientId { get; private set; }

    public Guid? CurrentReservationId { get; private set; }

    public DateTimeOffset? HoldExpiresAtUtc { get; private set; }

    public DateTimeOffset? OfferExpiresAtUtc { get; private set; }

    public int Generation { get; private set; }

    public IReadOnlyCollection<object> DomainEvents => _pendingEvents;

    public IReadOnlyList<object> DrainDomainEvents()
    {
        var events = _pendingEvents.ToArray();
        _pendingEvents.Clear();
        return events;
    }

    public bool IsWindowExpired(DateTimeOffset nowUtc) => EndsAtUtc <= nowUtc.ToUniversalTime();

    public bool IsAvailableAt(DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        return !IsWindowExpired(now) &&
               (State == SlotState.Available || State == SlotState.Held && HoldExpiresAtUtc <= now ||
                State == SlotState.Offered && OfferExpiresAtUtc <= now);
    }

    public bool TryHold(PatientId patientId, DateTimeOffset nowUtc, DateTimeOffset expiresAtUtc)
    {
        var now = nowUtc.ToUniversalTime();
        var expiresAt = expiresAtUtc.ToUniversalTime();
        ExpireLeaseForImmediateClaim(now);

        if (State != SlotState.Available || IsWindowExpired(now) || expiresAt <= now)
        {
            return false;
        }

        State = SlotState.Held;
        AssignedPatientId = patientId;
        HoldExpiresAtUtc = expiresAt;
        OfferExpiresAtUtc = null;
        Generation++;
        _pendingEvents.Add(new HoldConfirmationRequested(Guid.NewGuid(), Id.Value, patientId.Value,
            TestType.Value, Generation, expiresAt, now));
        return true;
    }

    public Reservation? TryConfirmHold(PatientId patientId, DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();

        if (State != SlotState.Held || AssignedPatientId != patientId || HoldExpiresAtUtc <= now ||
            IsWindowExpired(now))
        {
            return null;
        }

        State = SlotState.Confirmed;
        HoldExpiresAtUtc = null;
        Generation++;
        var reservation = Reservation.Create(Id, patientId, now);
        CurrentReservationId = reservation.Id;
        _pendingEvents.Add(new AppointmentConfirmed(Guid.NewGuid(), Id.Value, patientId.Value,
            reservation.Id, TestType.Value, now));
        return reservation;
    }

    public bool TryCancel(PatientId patientId, Guid reservationId, DateTimeOffset nowUtc)
    {
        if (State != SlotState.Confirmed || AssignedPatientId != patientId || CurrentReservationId != reservationId)
        {
            return false;
        }

        Release();
        RaiseReleased(nowUtc);
        return true;
    }

    public bool TryOffer(PatientId patientId, DateTimeOffset nowUtc, DateTimeOffset expiresAtUtc)
    {
        var now = nowUtc.ToUniversalTime();
        var expiresAt = expiresAtUtc.ToUniversalTime();
        ExpireLeaseForImmediateClaim(now);

        if (State != SlotState.Available || IsWindowExpired(now) || expiresAt <= now)
        {
            return false;
        }

        State = SlotState.Offered;
        AssignedPatientId = patientId;
        OfferExpiresAtUtc = expiresAt;
        HoldExpiresAtUtc = null;
        Generation++;
        _pendingEvents.Add(new OfferCreated(Guid.NewGuid(), Id.Value, patientId.Value, TestType.Value,
            Generation, expiresAt, now));
        return true;
    }

    public Reservation? TryAcceptOffer(PatientId patientId, int generation, DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();

        if (State != SlotState.Offered || AssignedPatientId != patientId || Generation != generation ||
            OfferExpiresAtUtc <= now || IsWindowExpired(now))
        {
            return null;
        }

        State = SlotState.Confirmed;
        OfferExpiresAtUtc = null;
        Generation++;
        var reservation = Reservation.Create(Id, patientId, now);
        CurrentReservationId = reservation.Id;
        _pendingEvents.Add(new AppointmentConfirmed(Guid.NewGuid(), Id.Value, patientId.Value,
            reservation.Id, TestType.Value, now));
        return reservation;
    }

    public bool TryDeclineOffer(PatientId patientId, int generation, DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        if (State != SlotState.Offered || AssignedPatientId != patientId || Generation != generation ||
            OfferExpiresAtUtc <= now)
        {
            return false;
        }

        Release();
        RaiseReleased(now);
        return true;
    }

    public bool ExpireLease(DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        if (!LeaseIsExpired(now))
        {
            return false;
        }

        Release();
        RaiseReleased(now);
        return true;
    }

    private void ExpireLeaseForImmediateClaim(DateTimeOffset nowUtc)
    {
        if (!LeaseIsExpired(nowUtc))
        {
            return;
        }

        State = SlotState.Available;
        AssignedPatientId = null;
        CurrentReservationId = null;
        HoldExpiresAtUtc = null;
        OfferExpiresAtUtc = null;
        Generation++;
    }

    private bool LeaseIsExpired(DateTimeOffset nowUtc) =>
        State == SlotState.Held && HoldExpiresAtUtc <= nowUtc ||
        State == SlotState.Offered && OfferExpiresAtUtc <= nowUtc;

    private void RaiseReleased(DateTimeOffset occurredAtUtc) =>
        _pendingEvents.Add(new SlotReleased(Guid.NewGuid(), Id.Value, TestType.Value, Generation,
            occurredAtUtc.ToUniversalTime()));

    private void Release()
    {
        State = SlotState.Available;
        AssignedPatientId = null;
        CurrentReservationId = null;
        HoldExpiresAtUtc = null;
        OfferExpiresAtUtc = null;
        Generation++;
    }
}
