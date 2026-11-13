using MediatR;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record ExpireSlotLease(Guid SlotId) : IRequest<bool>;
