using Recoletas.Booking.Domain.Entities;

namespace Recoletas.Booking.ApplicationServices.Abstractions;

public sealed record SlotWrapper<T>(Slot Slot, T Value);
