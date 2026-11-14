namespace Recoletas.Booking.Contracts.Events;

public static class BookingEventTypes
{
    public const string HoldConfirmationRequested = "booking.hold-confirmation-requested.v1";
    public const string SlotReleased = "booking.slot-released.v1";
    public const string OfferCreated = "booking.offer-created.v1";
    public const string AppointmentConfirmed = "booking.appointment-confirmed.v1";

    public const string LegacyHoldConfirmationRequested = "HoldConfirmationRequested";
    public const string LegacySlotReleased = "SlotReleased";
    public const string LegacyOfferCreated = "OfferCreated";
    public const string LegacyAppointmentConfirmed = "AppointmentConfirmed";
}
