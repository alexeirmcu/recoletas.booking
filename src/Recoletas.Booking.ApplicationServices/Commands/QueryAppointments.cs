using MediatR;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record QueryAppointments(Guid PatientId) : IRequest<IReadOnlyList<AppointmentSummary>>;
