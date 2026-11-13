using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Domain.Repository;

public interface ISlotRepository
{
    Task<Slot?> GetByIdAsync(SlotId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Slot>> ListAsync(CancellationToken cancellationToken);

    Task<Slot?> TryHoldAsync(SlotId id, PatientId patientId, DateTimeOffset nowUtc, DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken);

    Task<SlotWrapper<Reservation>?> TryConfirmAsync(SlotId id, PatientId patientId, int generation,
        DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<Slot?> TryCancelAsync(Guid reservationId, PatientId patientId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<Slot?> TryOfferAsync(SlotId id, PatientId patientId, DateTimeOffset nowUtc, DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken);

    Task<SlotWrapper<Reservation>?> TryAcceptOfferAsync(SlotId id, PatientId patientId, int generation,
        DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<Slot?> TryDeclineOfferAsync(SlotId id, PatientId patientId, int generation, DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<Slot?> TryExpireLeaseAsync(SlotId id, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task AddAsync(Slot slot, CancellationToken cancellationToken);
}
