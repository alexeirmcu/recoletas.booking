using MediatR;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.ValueObjects;

namespace Recoletas.Booking.ApplicationServices.Handlers;

public sealed class QueryAgendaHandler(ISlotRepository slots, IClock clock)
    : IRequestHandler<QueryAgenda, IReadOnlyList<SlotSummary>>
{
    public async Task<IReadOnlyList<SlotSummary>> Handle(QueryAgenda request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.ToUniversalTime();
        var from = request.From.ToUniversalTime();
        var to = request.To.ToUniversalTime();
        if (to <= from)
        {
            return Array.Empty<SlotSummary>();
        }

        var testType = string.IsNullOrWhiteSpace(request.TestType) ? null : new TestType(request.TestType);
        return (await slots.ListAsync(cancellationToken))
            .Where(slot => slot.IsAvailableAt(now))
            .Where(slot => testType is null || slot.TestType == testType)
            .Where(slot => slot.StartsAtUtc >= from && slot.StartsAtUtc < to)
            .OrderBy(slot => slot.StartsAtUtc)
            .Select(slot => new SlotSummary(slot.Id.Value, slot.TestType.Value,
                slot.StartsAtUtc, slot.EndsAtUtc, "Available"))
            .ToArray();
    }
}
