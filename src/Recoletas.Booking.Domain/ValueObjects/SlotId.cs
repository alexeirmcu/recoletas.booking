namespace Recoletas.Booking.Domain.ValueObjects;

public readonly record struct SlotId
{
    public SlotId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A slot identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static SlotId New() => new(Guid.NewGuid());
}
