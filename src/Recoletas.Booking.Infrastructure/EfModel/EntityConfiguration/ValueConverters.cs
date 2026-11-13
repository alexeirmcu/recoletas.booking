using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.Infrastructure.EfModel.EntityConfiguration;

internal static class ValueConverters
{
    internal static readonly ValueConverter<SlotId, Guid> SlotIdToGuid =
        new(id => id.Value, value => new SlotId(value));

    internal static readonly ValueConverter<PatientId, Guid> PatientIdToGuid =
        new(id => id.Value, value => new PatientId(value));

    internal static readonly ValueConverter<TestType, string> TestTypeToString =
        new(type => type.Value, value => new TestType(value));
}
