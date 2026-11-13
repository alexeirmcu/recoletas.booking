using MediatR;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record CancelAppointment(Guid ReservationId, Guid PatientId) : IRequest<BookingResult>;
