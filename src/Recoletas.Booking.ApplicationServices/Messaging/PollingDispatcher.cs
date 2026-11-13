using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.Services;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.Notifications;
using Recoletas.Booking.Infrastructure.Outbox;
using Microsoft.Extensions.Logging;

namespace Recoletas.Booking.ApplicationServices.Messaging;

public sealed class PollingDispatcher(ISlotRepository slots, IMediator mediator,
    INotificationSender notificationSender, IClock clock, ILogger<PollingDispatcher> logger) : IEventDispatcher
{
    public async Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var integrationEvent = OutboxStore.DeserializeEvent(message);
        switch (integrationEvent)
        {
            case SlotReleased released:
                await DispatchSlotReleasedAsync(released, cancellationToken);
                break;
            case OfferCreated offer:
                await DispatchOfferCreatedAsync(offer, cancellationToken);
                break;
            case AppointmentConfirmed confirmed:
                await notificationSender.SendAsync(new NotificationRequest(confirmed.EventId,
                    nameof(AppointmentConfirmed),
                    confirmed.PatientId.ToString(), "Appointment confirmed",
                    $"Appointment {confirmed.ReservationId} is confirmed."), cancellationToken);
                break;
            case HoldConfirmationRequested holdConfirmation:
                await DispatchHoldConfirmationRequestedAsync(holdConfirmation, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unsupported booking event '{integrationEvent.GetType().Name}'.");
        }
    }

    private async Task DispatchSlotReleasedAsync(SlotReleased released, CancellationToken cancellationToken)
    {
        var slot = await slots.GetByIdAsync(new SlotId(released.SlotId), cancellationToken);
        if (slot is null || slot.State != SlotState.Available || slot.Generation != released.Generation)
        {
            return;
        }

        await mediator.Send(new OfferNextWaiter(released.SlotId), cancellationToken);
    }

    private async Task DispatchOfferCreatedAsync(OfferCreated offer, CancellationToken cancellationToken)
    {
        var slot = await slots.GetByIdAsync(new SlotId(offer.SlotId), cancellationToken);
        if (slot is null || slot.State != SlotState.Offered || slot.Generation != offer.Generation ||
            slot.AssignedPatientId != new PatientId(offer.PatientId))
        {
            return;
        }

        await notificationSender.SendAsync(new NotificationRequest(offer.EventId, nameof(OfferCreated),
            offer.PatientId.ToString(),
            "Appointment slot available", $"Slot {offer.SlotId} is available until {offer.ExpiresAtUtc:O}."),
            cancellationToken);
    }

    private async Task DispatchHoldConfirmationRequestedAsync(HoldConfirmationRequested holdConfirmation,
        CancellationToken cancellationToken)
    {
        var slot = await slots.GetByIdAsync(new SlotId(holdConfirmation.SlotId), cancellationToken);
        var now = clock.UtcNow.ToUniversalTime();
        var holdExpiresAtUtc = slot?.HoldExpiresAtUtc;
        if (slot is null || slot.State != SlotState.Held ||
            slot.AssignedPatientId != new PatientId(holdConfirmation.PatientId) ||
            slot.Generation != holdConfirmation.Generation || holdExpiresAtUtc is null ||
            holdExpiresAtUtc <= now || holdExpiresAtUtc != holdConfirmation.ExpiresAtUtc ||
            holdConfirmation.ExpiresAtUtc <= now)
        {
            logger.LogDebug("Ignoring stale or expired event {EventId} of type {EventType}.",
                holdConfirmation.EventId, nameof(HoldConfirmationRequested));
            return;
        }

        await notificationSender.SendAsync(new NotificationRequest(holdConfirmation.EventId,
            nameof(HoldConfirmationRequested), holdConfirmation.PatientId.ToString(), "Please confirm your appointment",
            $"Please confirm your appointment before the hold expires at {holdConfirmation.ExpiresAtUtc:O}."),
            cancellationToken);
    }
}
