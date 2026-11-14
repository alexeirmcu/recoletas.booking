using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Abstractions;

public interface IWaitlistRepository
{
    Task<bool> TryAddAsync(WaitlistEntry entry, CancellationToken cancellationToken);

    Task<WaitlistEntry?> GetActiveEntryAsync(PatientId patientId, TestType testType,
        CancellationToken cancellationToken);

    Task<WaitlistEntry?> GetNextAsync(TestType testType, CancellationToken cancellationToken);

    Task<bool> TryClaimAsync(Guid entryId, CancellationToken cancellationToken);

    Task<bool> TryRestoreClaimAsync(Guid entryId, CancellationToken cancellationToken);

    Task RemoveAsync(Guid entryId, CancellationToken cancellationToken);
}
