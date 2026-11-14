using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Recoletas.Booking.ApplicationServices.Messaging;

public sealed class NotificationEventHandler(ISlotRepository slots, INotificationSender notificationSender,
    IClock clock, ILogger<NotificationEventHandler> logger)
{
    public async Task HandleAsync(AppointmentConfirmed confirmed, CancellationToken cancellationToken) =>
        await notificationSender.SendAsync(new NotificationRequest(confirmed.EventId,
            BookingEventTypes.AppointmentConfirmed, confirmed.PatientId.ToString(), "Appointment confirmed",
            $"Appointment {confirmed.ReservationId} is confirmed."), cancellationToken);

    public async Task HandleAsync(OfferCreated offer, CancellationToken cancellationToken)
    {
        var slot = await slots.GetByIdAsync(new SlotId(offer.SlotId), cancellationToken);
        var expiresAtUtc = slot?.OfferExpiresAtUtc;
        var now = clock.UtcNow;
        if (slot is null || slot.State != SlotState.Offered || slot.Generation != offer.Generation ||
            slot.AssignedPatientId != new PatientId(offer.PatientId) ||
            !ExpirationMatches(expiresAtUtc, offer.ExpiresAtUtc) ||
            expiresAtUtc <= now || offer.ExpiresAtUtc <= now)
        {
            return;
        }

        await notificationSender.SendAsync(new NotificationRequest(offer.EventId, BookingEventTypes.OfferCreated,
            offer.PatientId.ToString(), "Appointment slot available",
            $"Slot {offer.SlotId} is available until {offer.ExpiresAtUtc:O}.",
            Generation: offer.Generation), cancellationToken);
    }

    public async Task HandleAsync(HoldConfirmationRequested holdConfirmation,
        CancellationToken cancellationToken)
    {
        var slot = await slots.GetByIdAsync(new SlotId(holdConfirmation.SlotId), cancellationToken);
        var now = clock.UtcNow.ToUniversalTime();
        var holdExpiresAtUtc = slot?.HoldExpiresAtUtc;
        if (slot is null || slot.State != SlotState.Held ||
            slot.AssignedPatientId != new PatientId(holdConfirmation.PatientId) ||
            slot.Generation != holdConfirmation.Generation || holdExpiresAtUtc is null ||
            holdExpiresAtUtc <= now || !ExpirationMatches(holdExpiresAtUtc, holdConfirmation.ExpiresAtUtc) ||
            holdConfirmation.ExpiresAtUtc <= now)
        {
            logger.LogDebug("Ignoring stale or expired event {EventId} of type {EventType}.",
                holdConfirmation.EventId, BookingEventTypes.HoldConfirmationRequested);
            return;
        }

        await notificationSender.SendAsync(new NotificationRequest(holdConfirmation.EventId,
            BookingEventTypes.HoldConfirmationRequested, holdConfirmation.PatientId.ToString(),
            "Please confirm your appointment",
            $"Please confirm your appointment before the hold expires at {holdConfirmation.ExpiresAtUtc:O}.",
            Generation: holdConfirmation.Generation),
            cancellationToken);
    }

    private static bool ExpirationMatches(DateTimeOffset? persistedExpiry, DateTimeOffset eventExpiry) =>
        // PostgreSQL stores microseconds; JSON events retain .NET's 100-nanosecond ticks.
        persistedExpiry.HasValue &&
        persistedExpiry.Value.UtcTicks / TimeSpan.TicksPerMicrosecond ==
        eventExpiry.UtcTicks / TimeSpan.TicksPerMicrosecond;
}
