using MediatR;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record AcceptOffer(Guid SlotId, Guid PatientId, AcceptOfferRequest Request) : IRequest<BookingResult>;
