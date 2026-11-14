using MediatR;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class QueryAdminAppointmentsHandler(IAdminAppointmentReadRepository appointments)
    : IRequestHandler<QueryAdminAppointments, IReadOnlyList<AdminAppointmentSummary>>
{
    public Task<IReadOnlyList<AdminAppointmentSummary>> Handle(
        QueryAdminAppointments request,
        CancellationToken cancellationToken) =>
        appointments.ListConfirmedAsync(
            request.From.ToUniversalTime(),
            request.To.ToUniversalTime(),
            cancellationToken);
}
