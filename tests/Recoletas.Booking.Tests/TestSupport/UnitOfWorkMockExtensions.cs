using Moq;
using Recoletas.Booking.Domain.Repository;

namespace Recoletas.Booking.Tests.TestSupport;

public static class UnitOfWorkMockExtensions
{
    public static void ExecuteInline<T>(this Mock<IUnitOfWork> unitOfWork)
    {
        unitOfWork.Setup(work => work.ExecuteAsync(It.IsAny<Func<CancellationToken, Task<T>>>(),
                It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task<T>> operation, CancellationToken token) => operation(token));
    }
}
