using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Abstractions;

public interface IAdminAppointmentReadRepository
{
    Task<IReadOnlyList<AdminAppointmentSummary>> ListConfirmedAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}
