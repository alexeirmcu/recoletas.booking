namespace Recoletas.Booking.Infrastructure.EfModel;

public interface IConstraintViolationDetector
{
    bool IsUniqueConstraintViolation(Exception exception);
}
