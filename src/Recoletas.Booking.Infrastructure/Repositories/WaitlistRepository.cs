using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.EfModel;
using Microsoft.EntityFrameworkCore;

namespace Recoletas.Booking.Infrastructure.Repositories;

public sealed class WaitlistRepository(BookingDbContext dbContext,
    IConstraintViolationDetector constraintViolationDetector) : IWaitlistRepository
{
    public async Task<bool> TryAddAsync(WaitlistEntry entry, CancellationToken cancellationToken)
    {
        dbContext.WaitlistEntries.Add(entry);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when
            (constraintViolationDetector.IsUniqueConstraintViolation(exception))
        {
            dbContext.Entry(entry).State = EntityState.Detached;
            return false;
        }
    }

    public Task<WaitlistEntry?> GetActiveEntryAsync(PatientId patientId, TestType testType,
        CancellationToken cancellationToken) =>
        dbContext.WaitlistEntries.AsNoTracking().SingleOrDefaultAsync(entry =>
            entry.PatientId == patientId && entry.TestType == testType && entry.IsActive, cancellationToken);

    public Task<WaitlistEntry?> GetNextAsync(TestType testType, CancellationToken cancellationToken) =>
        dbContext.WaitlistEntries.AsNoTracking()
            .Where(entry => entry.TestType == testType && entry.IsActive)
            .OrderBy(entry => entry.JoinedAtUtc)
            .ThenBy(entry => entry.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task RemoveAsync(Guid entryId, CancellationToken cancellationToken)
    {
        await dbContext.WaitlistEntries.Where(entry => entry.Id == entryId && entry.IsActive)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.IsActive, false), cancellationToken);
    }
}
