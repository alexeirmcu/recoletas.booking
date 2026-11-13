using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Domain.Repository;

public interface IWaitlistRepository
{
    Task<bool> TryAddAsync(WaitlistEntry entry, CancellationToken cancellationToken);

    Task<WaitlistEntry?> GetActiveEntryAsync(PatientId patientId, TestType testType,
        CancellationToken cancellationToken);

    Task<WaitlistEntry?> GetNextAsync(TestType testType, CancellationToken cancellationToken);

    Task RemoveAsync(Guid entryId, CancellationToken cancellationToken);
}
