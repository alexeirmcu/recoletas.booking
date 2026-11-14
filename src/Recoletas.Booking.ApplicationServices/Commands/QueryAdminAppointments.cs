using MediatR;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record QueryAdminAppointments(DateTimeOffset From, DateTimeOffset To)
    : IRequest<IReadOnlyList<AdminAppointmentSummary>>;
