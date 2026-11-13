using MediatR;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record BlockSlot(Guid SlotId, Guid PatientId) : IRequest<BookingResult>;
