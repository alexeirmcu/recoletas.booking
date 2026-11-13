namespace Recoletas.Booking.Domain.ValueObjects;

public sealed record TestType
{
    private static readonly IReadOnlyList<TestType> SupportedValues = Array.AsReadOnly(
    [
        new TestType("BloodTest"),
        new TestType("Ultrasound"),
        new TestType("MRI")
    ]);

    public TestType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public static IReadOnlyList<TestType> SupportedTypes => SupportedValues;

    public static bool IsSupported(string? value) =>
        SupportedValues.Any(testType => string.Equals(testType.Value, value, StringComparison.Ordinal));

    public override string ToString() => Value;
}
