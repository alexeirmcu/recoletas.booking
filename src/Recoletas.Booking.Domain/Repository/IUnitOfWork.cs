namespace Recoletas.Booking.Domain.Repository;

/// <summary>Runs application state changes and outbox writes as one atomic operation.</summary>
public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}
