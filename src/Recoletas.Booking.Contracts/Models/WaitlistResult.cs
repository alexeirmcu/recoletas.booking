namespace Recoletas.Booking.Contracts.Models;

public sealed record WaitlistResult(bool Joined, bool AlreadyJoined, string? ErrorCode = null);
