using Recoletas.Booking.Domain.Entities;

namespace Recoletas.Booking.Domain.Repository;

public sealed record SlotWrapper<T>(Slot Slot, T Value);
