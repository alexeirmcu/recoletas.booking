using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Infrastructure.EfModel;

public sealed class TestTypeCatalogEntry
{
    private TestTypeCatalogEntry()
    {
    }

    public TestTypeCatalogEntry(TestType code) => Code = code;

    public TestType Code { get; private set; } = null!;
}
