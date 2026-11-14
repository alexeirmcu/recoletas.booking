using System.Text.Json;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.ApplicationServices.Outbox;
using Recoletas.Booking.Infrastructure.EfModel;
using Microsoft.EntityFrameworkCore;

namespace Recoletas.Booking.Infrastructure.Outbox;

public sealed class OutboxStore(BookingDbContext dbContext, IClock clock) : IOutboxStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    public Task EnqueueAsync(HoldConfirmationRequested integrationEvent, CancellationToken cancellationToken) =>
        EnqueueEventAsync(integrationEvent, BookingEventTypes.HoldConfirmationRequested, cancellationToken);

    public Task EnqueueAsync(SlotReleased integrationEvent, CancellationToken cancellationToken) =>
        EnqueueEventAsync(integrationEvent, BookingEventTypes.SlotReleased, cancellationToken);

    public Task EnqueueAsync(OfferCreated integrationEvent, CancellationToken cancellationToken) =>
        EnqueueEventAsync(integrationEvent, BookingEventTypes.OfferCreated, cancellationToken);

    public Task EnqueueAsync(AppointmentConfirmed integrationEvent, CancellationToken cancellationToken) =>
        EnqueueEventAsync(integrationEvent, BookingEventTypes.AppointmentConfirmed, cancellationToken);

    public async Task<IReadOnlyList<OutboxMessage>> LeaseAsync(int batchSize,
        CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
        {
            return Array.Empty<OutboxMessage>();
        }

        var now = clock.UtcNow.ToUniversalTime();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var candidates = await dbContext.OutboxMessages.AsNoTracking()
            .Where(message => message.ProcessedAtUtc == null && message.DeadAtUtc == null &&
                              message.AvailableAtUtc <= now &&
                              (message.LeaseUntilUtc == null || message.LeaseUntilUtc <= now))
            .OrderBy(message => message.OccurredAtUtc)
            .ThenBy(message => message.Id)
            .Take(batchSize)
            .Select(message => new
            {
                message.Id, message.EventType, message.Payload, message.OccurredAtUtc, message.Attempts
            })
            .ToListAsync(cancellationToken);
        var leased = new List<OutboxMessage>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var leaseToken = Guid.NewGuid();
            var updated = await dbContext.OutboxMessages.Where(message => message.Id == candidate.Id &&
                    message.ProcessedAtUtc == null && message.DeadAtUtc == null &&
                    message.AvailableAtUtc <= now &&
                    (message.LeaseUntilUtc == null || message.LeaseUntilUtc <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.LeaseUntilUtc, now.AddMinutes(5))
                    .SetProperty(message => message.LeaseToken, (Guid?)leaseToken)
                    .SetProperty(message => message.Attempts, message => message.Attempts + 1), cancellationToken);
            if (updated == 1)
            {
                leased.Add(new OutboxMessage(candidate.Id, candidate.EventType, candidate.Payload,
                    candidate.OccurredAtUtc, leaseToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return leased;
    }

    public async Task<bool> MarkProcessedAsync(Guid messageId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.ToUniversalTime();
        return await dbContext.OutboxMessages.Where(message => message.Id == messageId &&
                message.LeaseToken == leaseToken && message.LeaseUntilUtc > now &&
                message.ProcessedAtUtc == null && message.DeadAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.ProcessedAtUtc, now)
                .SetProperty(message => message.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LeaseToken, (Guid?)null), cancellationToken) == 1;
    }

    public async Task<OutboxFailureResult> RecordFailureAsync(Guid messageId, Guid leaseToken, string reason,
        TimeSpan retryDelay,
        int maxAttempts, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.ToUniversalTime();
        var item = await dbContext.OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(message => message.Id == messageId && message.LeaseToken == leaseToken &&
                                              message.LeaseUntilUtc > now && message.ProcessedAtUtc == null &&
                                              message.DeadAtUtc == null, cancellationToken);
        if (item is null)
        {
            return OutboxFailureResult.LeaseLost;
        }

        if (item.Attempts >= maxAttempts)
        {
            var deadLettered = await dbContext.OutboxMessages.Where(message => message.Id == messageId &&
                    message.LeaseToken == leaseToken && message.LeaseUntilUtc > now &&
                    message.ProcessedAtUtc == null && message.DeadAtUtc == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.DeadAtUtc, now)
                    .SetProperty(message => message.LeaseUntilUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.LeaseToken, (Guid?)null)
                    .SetProperty(message => message.LastError, Truncate(reason)), cancellationToken) == 1;
            return deadLettered ? OutboxFailureResult.DeadLettered : OutboxFailureResult.LeaseLost;
        }

        var multiplier = Math.Pow(2, Math.Clamp(item.Attempts - 1, 0, 10));
        var effectiveDelay = TimeSpan.FromTicks((long)(retryDelay.Ticks * multiplier));
        var retryScheduled = await dbContext.OutboxMessages.Where(message => message.Id == messageId &&
                message.LeaseToken == leaseToken && message.LeaseUntilUtc > now &&
                message.ProcessedAtUtc == null && message.DeadAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.AvailableAtUtc, now.Add(effectiveDelay))
                .SetProperty(message => message.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LeaseToken, (Guid?)null)
                .SetProperty(message => message.LastError, Truncate(reason)), cancellationToken) == 1;
        return retryScheduled ? OutboxFailureResult.RetryScheduled : OutboxFailureResult.LeaseLost;
    }

    private static string Truncate(string reason) => reason.Length <= 2000 ? reason : reason[..2000];

    private Task EnqueueEventAsync<TEvent>(TEvent integrationEvent, string eventType,
        CancellationToken cancellationToken) where TEvent : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        var record = integrationEvent switch
        {
            HoldConfirmationRequested holdConfirmation => CreateRecord(holdConfirmation.EventId, eventType,
                holdConfirmation, holdConfirmation.OccurredAtUtc),
            SlotReleased released => CreateRecord(released.EventId, eventType, released, released.OccurredAtUtc),
            OfferCreated offer => CreateRecord(offer.EventId, eventType, offer, offer.OccurredAtUtc),
            AppointmentConfirmed confirmed => CreateRecord(confirmed.EventId, eventType, confirmed,
                confirmed.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported outbox event type '{eventType}'.")
        };
        record.AvailableAtUtc = clock.UtcNow.ToUniversalTime();
        dbContext.OutboxMessages.Add(record);
        return Task.CompletedTask;
    }

    private static OutboxRecord CreateRecord<TEvent>(Guid eventId, string eventType, TEvent integrationEvent,
        DateTimeOffset occurredAtUtc) => new()
    {
        Id = eventId,
        EventType = eventType,
        Payload = JsonSerializer.Serialize(integrationEvent, SerializerOptions),
        OccurredAtUtc = occurredAtUtc.ToUniversalTime()
    };
}
