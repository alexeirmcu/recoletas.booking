using MediatR;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record ConfirmHold(Guid SlotId, Guid PatientId, ConfirmHoldRequest Request) : IRequest<BookingResult>;
