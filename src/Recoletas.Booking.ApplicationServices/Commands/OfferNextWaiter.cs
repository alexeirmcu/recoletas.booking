using MediatR;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record OfferNextWaiter(Guid SlotId, int? ExpectedGeneration = null) : IRequest<bool>;
