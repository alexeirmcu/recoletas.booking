namespace Recoletas.Booking.Domain.ValueObjects;

public readonly record struct PatientId
{
    public PatientId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A patient identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static PatientId New() => new(Guid.NewGuid());
}
