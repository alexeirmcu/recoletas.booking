using MediatR;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record DeclineOffer(Guid SlotId, Guid PatientId, DeclineOfferRequest Request) : IRequest<BookingResult>;
