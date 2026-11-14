namespace Recoletas.Booking.ApplicationServices.Outbox;

public enum OutboxFailureResult
{
    RetryScheduled,
    DeadLettered,
    LeaseLost
}
