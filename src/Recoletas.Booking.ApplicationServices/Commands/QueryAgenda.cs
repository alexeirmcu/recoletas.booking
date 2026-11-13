using MediatR;
using Recoletas.Booking.Contracts.Models;

namespace Recoletas.Booking.ApplicationServices.Commands;

public sealed record QueryAgenda(string? TestType, DateTimeOffset From, DateTimeOffset To)
    : IRequest<IReadOnlyList<SlotSummary>>;
