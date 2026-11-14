using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.EfModel;
using Microsoft.EntityFrameworkCore;

namespace Recoletas.Booking.Infrastructure.Repositories;

public sealed class SlotRepository(BookingDbContext dbContext) : ISlotRepository
{
    public Task<Slot?> GetByIdAsync(SlotId id, CancellationToken cancellationToken) =>
        dbContext.Slots.AsNoTracking().SingleOrDefaultAsync(slot => slot.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Slot>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.Slots.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Slot>> ListPendingForPatientAsync(PatientId patientId,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();
        return await dbContext.Slots.AsNoTracking()
            .Where(slot => slot.AssignedPatientId == patientId && slot.EndsAtUtc > now &&
                           (slot.State == SlotState.Held && slot.HoldExpiresAtUtc != null &&
                            slot.HoldExpiresAtUtc > now ||
                            slot.State == SlotState.Offered && slot.OfferExpiresAtUtc != null &&
                            slot.OfferExpiresAtUtc > now))
            .OrderBy(slot => slot.State == SlotState.Held
                ? slot.HoldExpiresAtUtc
                : slot.OfferExpiresAtUtc)
            .ThenBy(slot => slot.StartsAtUtc)
            .ThenBy(slot => slot.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<Slot?> TryHoldAsync(SlotId id, PatientId patientId, DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();
        var expiresAt = expiresAtUtc.ToUniversalTime();
        var slot = await GetByIdAsync(id, cancellationToken);
        if (slot is null)
        {
            return null;
        }

        var originalGeneration = slot.Generation;
        if (!slot.TryHold(patientId, now, expiresAt))
        {
            return null;
        }

        var updated = await dbContext.Slots.Where(candidate => candidate.Id == id &&
                candidate.Generation == originalGeneration && candidate.EndsAtUtc > now &&
                (candidate.State == SlotState.Available ||
                 candidate.State == SlotState.Held && candidate.HoldExpiresAtUtc <= now ||
                 candidate.State == SlotState.Offered && candidate.OfferExpiresAtUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.State, slot.State)
                .SetProperty(candidate => candidate.AssignedPatientId, (PatientId?)patientId)
                .SetProperty(candidate => candidate.CurrentReservationId, (Guid?)null)
                .SetProperty(candidate => candidate.HoldExpiresAtUtc, (DateTimeOffset?)expiresAt)
                .SetProperty(candidate => candidate.OfferExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.Generation, slot.Generation), cancellationToken);

        return updated == 1 ? slot : null;
    }

    public async Task<SlotWrapper<Reservation>?> TryConfirmAsync(SlotId id, PatientId patientId,
        int generation, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();
        var slot = await GetByIdAsync(id, cancellationToken);
        if (slot is null || slot.Generation != generation)
        {
            return null;
        }

        var reservation = slot.TryConfirmHold(patientId, now);
        if (reservation is null)
        {
            return null;
        }

        var updated = await dbContext.Slots.Where(candidate => candidate.Id == id &&
                candidate.State == SlotState.Held && candidate.AssignedPatientId == patientId &&
                candidate.Generation == generation && candidate.HoldExpiresAtUtc > now && candidate.EndsAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.State, slot.State)
                .SetProperty(candidate => candidate.HoldExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.CurrentReservationId, (Guid?)reservation.Id)
                .SetProperty(candidate => candidate.Generation, slot.Generation), cancellationToken);
        if (updated != 1)
        {
            return null;
        }

        dbContext.Reservations.Add(reservation);
        return new SlotWrapper<Reservation>(slot, reservation);
    }

    public async Task<Slot?> TryCancelAsync(Guid reservationId, PatientId patientId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var slot = await dbContext.Slots.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.CurrentReservationId == reservationId && candidate.AssignedPatientId == patientId,
            cancellationToken);
        if (slot is null)
        {
            return null;
        }

        var originalGeneration = slot.Generation;
        if (!slot.TryCancel(patientId, reservationId, nowUtc))
        {
            return null;
        }

        var updated = await dbContext.Slots.Where(candidate => candidate.Id == slot.Id &&
                candidate.State == SlotState.Confirmed && candidate.AssignedPatientId == patientId &&
                candidate.CurrentReservationId == reservationId && candidate.Generation == originalGeneration)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.State, slot.State)
                .SetProperty(candidate => candidate.AssignedPatientId, (PatientId?)null)
                .SetProperty(candidate => candidate.CurrentReservationId, (Guid?)null)
                .SetProperty(candidate => candidate.HoldExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.OfferExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.Generation, slot.Generation), cancellationToken);
        return updated == 1 ? slot : null;
    }

    public async Task<Slot?> TryOfferAsync(SlotId id, PatientId patientId, DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc, CancellationToken cancellationToken, int? expectedGeneration = null)
    {
        var now = nowUtc.ToUniversalTime();
        var expiresAt = expiresAtUtc.ToUniversalTime();
        var slot = await GetByIdAsync(id, cancellationToken);
        if (slot is null)
        {
            return null;
        }

        var originalGeneration = slot.Generation;
        if (!slot.TryOffer(patientId, now, expiresAt))
        {
            return null;
        }

        var updated = await dbContext.Slots.Where(candidate => candidate.Id == id &&
                candidate.Generation == originalGeneration &&
                (expectedGeneration == null || candidate.Generation == expectedGeneration) && candidate.EndsAtUtc > now &&
                (candidate.State == SlotState.Available ||
                 candidate.State == SlotState.Held && candidate.HoldExpiresAtUtc <= now ||
                 candidate.State == SlotState.Offered && candidate.OfferExpiresAtUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.State, slot.State)
                .SetProperty(candidate => candidate.AssignedPatientId, (PatientId?)patientId)
                .SetProperty(candidate => candidate.CurrentReservationId, (Guid?)null)
                .SetProperty(candidate => candidate.HoldExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.OfferExpiresAtUtc, (DateTimeOffset?)expiresAt)
                .SetProperty(candidate => candidate.Generation, slot.Generation), cancellationToken);

        return updated == 1 ? slot : null;
    }

    public async Task<SlotWrapper<Reservation>?> TryAcceptOfferAsync(SlotId id, PatientId patientId,
        int generation, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();
        var slot = await GetByIdAsync(id, cancellationToken);
        if (slot is null || slot.Generation != generation)
        {
            return null;
        }

        var reservation = slot.TryAcceptOffer(patientId, generation, now);
        if (reservation is null)
        {
            return null;
        }

        var updated = await dbContext.Slots.Where(candidate => candidate.Id == id &&
                candidate.State == SlotState.Offered && candidate.AssignedPatientId == patientId &&
                candidate.Generation == generation && candidate.OfferExpiresAtUtc > now && candidate.EndsAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.State, slot.State)
                .SetProperty(candidate => candidate.OfferExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.CurrentReservationId, (Guid?)reservation.Id)
                .SetProperty(candidate => candidate.Generation, slot.Generation), cancellationToken);
        if (updated != 1)
        {
            return null;
        }

        dbContext.Reservations.Add(reservation);
        return new SlotWrapper<Reservation>(slot, reservation);
    }

    public async Task<Slot?> TryDeclineOfferAsync(SlotId id, PatientId patientId, int generation,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();
        var slot = await GetByIdAsync(id, cancellationToken);
        if (slot is null || slot.Generation != generation || !slot.TryDeclineOffer(patientId, generation, now))
        {
            return null;
        }

        var updated = await dbContext.Slots.Where(candidate => candidate.Id == id &&
                candidate.State == SlotState.Offered && candidate.AssignedPatientId == patientId &&
                candidate.Generation == generation && candidate.OfferExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.State, slot.State)
                .SetProperty(candidate => candidate.AssignedPatientId, (PatientId?)null)
                .SetProperty(candidate => candidate.CurrentReservationId, (Guid?)null)
                .SetProperty(candidate => candidate.OfferExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.Generation, slot.Generation), cancellationToken);

        return updated == 1 ? slot : null;
    }

    public async Task<Slot?> TryExpireLeaseAsync(SlotId id, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();
        var slot = await GetByIdAsync(id, cancellationToken);
        if (slot is null)
        {
            return null;
        }

        var originalGeneration = slot.Generation;
        if (!slot.ExpireLease(now))
        {
            return null;
        }

        var updated = await dbContext.Slots.Where(candidate => candidate.Id == id &&
                candidate.Generation == originalGeneration &&
                (candidate.State == SlotState.Held && candidate.HoldExpiresAtUtc <= now ||
                 candidate.State == SlotState.Offered && candidate.OfferExpiresAtUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.State, slot.State)
                .SetProperty(candidate => candidate.AssignedPatientId, (PatientId?)null)
                .SetProperty(candidate => candidate.CurrentReservationId, (Guid?)null)
                .SetProperty(candidate => candidate.HoldExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.OfferExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.Generation, slot.Generation), cancellationToken);

        return updated == 1 ? slot : null;
    }

    public async Task AddAsync(Slot slot, CancellationToken cancellationToken)
    {
        dbContext.Slots.Add(slot);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
