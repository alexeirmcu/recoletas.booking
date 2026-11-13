using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Recoletas.Booking.Infrastructure.EfModel;

public sealed class PostgresConstraintViolationDetector : IConstraintViolationDetector
{
    public bool IsUniqueConstraintViolation(Exception exception) =>
        exception is DbUpdateException { InnerException: PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation } };
}
