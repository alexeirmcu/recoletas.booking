using MediatR;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record QueryPendingAppointments(Guid PatientId) :
    IRequest<IReadOnlyList<PendingAppointmentSummary>>;
