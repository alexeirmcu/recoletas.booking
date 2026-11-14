using Recoletas.Booking.ApplicationServices.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Recoletas.Booking.Infrastructure.EfModel;

public sealed class UnitOfWork(BookingDbContext dbContext) : IUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var existingTransaction = dbContext.Database.CurrentTransaction;
        if (existingTransaction is not null)
        {
            var nestedResult = await operation(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return nestedResult;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await operation(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }
}
