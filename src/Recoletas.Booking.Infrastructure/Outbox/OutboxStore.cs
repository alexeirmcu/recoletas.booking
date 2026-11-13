using System.Text.Json;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.Services;
using Recoletas.Booking.Infrastructure.EfModel;
using Microsoft.EntityFrameworkCore;

namespace Recoletas.Booking.Infrastructure.Outbox;

public sealed class OutboxStore(BookingDbContext dbContext, IClock clock) : IOutboxStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, Type> EventTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [nameof(HoldConfirmationRequested)] = typeof(HoldConfirmationRequested),
        [nameof(SlotReleased)] = typeof(SlotReleased),
        [nameof(OfferCreated)] = typeof(OfferCreated),
        [nameof(AppointmentConfirmed)] = typeof(AppointmentConfirmed)
    };

    public Task EnqueueAsync(HoldConfirmationRequested integrationEvent, CancellationToken cancellationToken) =>
        EnqueueEventAsync(integrationEvent, nameof(HoldConfirmationRequested), cancellationToken);

    public Task EnqueueAsync(SlotReleased integrationEvent, CancellationToken cancellationToken) =>
        EnqueueEventAsync(integrationEvent, nameof(SlotReleased), cancellationToken);

    public Task EnqueueAsync(OfferCreated integrationEvent, CancellationToken cancellationToken) =>
        EnqueueEventAsync(integrationEvent, nameof(OfferCreated), cancellationToken);

    public Task EnqueueAsync(AppointmentConfirmed integrationEvent, CancellationToken cancellationToken) =>
        EnqueueEventAsync(integrationEvent, nameof(AppointmentConfirmed), cancellationToken);

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
            .Select(message => new { message.Id, message.EventType, message.Payload, message.OccurredAtUtc })
            .ToListAsync(cancellationToken);
        var leased = new List<OutboxMessage>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var updated = await dbContext.OutboxMessages.Where(message => message.Id == candidate.Id &&
                    message.ProcessedAtUtc == null && message.DeadAtUtc == null &&
                    message.AvailableAtUtc <= now &&
                    (message.LeaseUntilUtc == null || message.LeaseUntilUtc <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.LeaseUntilUtc, now.AddMinutes(1))
                    .SetProperty(message => message.Attempts, message => message.Attempts + 1), cancellationToken);
            if (updated == 1)
            {
                leased.Add(new OutboxMessage(candidate.Id, candidate.EventType, candidate.Payload,
                    candidate.OccurredAtUtc));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return leased;
    }

    public async Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.ToUniversalTime();
        await dbContext.OutboxMessages.Where(message => message.Id == messageId && message.DeadAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.ProcessedAtUtc, now)
                .SetProperty(message => message.LeaseUntilUtc, (DateTimeOffset?)null), cancellationToken);
    }

    public async Task MarkDeadAsync(Guid messageId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.ToUniversalTime();
        await dbContext.OutboxMessages.Where(message => message.Id == messageId && message.ProcessedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.DeadAtUtc, now)
                .SetProperty(message => message.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LastError, Truncate(reason)), cancellationToken);
    }

    public async Task<bool> RecordFailureAsync(Guid messageId, string reason, TimeSpan retryDelay,
        int maxAttempts, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow.ToUniversalTime();
        var item = await dbContext.OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(message => message.Id == messageId, cancellationToken);
        if (item is null || item.ProcessedAtUtc is not null || item.DeadAtUtc is not null)
        {
            return false;
        }

        if (item.Attempts >= maxAttempts)
        {
            await MarkDeadAsync(messageId, reason, cancellationToken);
            return true;
        }

        var multiplier = Math.Pow(2, Math.Clamp(item.Attempts - 1, 0, 10));
        var effectiveDelay = TimeSpan.FromTicks((long)(retryDelay.Ticks * multiplier));
        await dbContext.OutboxMessages.Where(message => message.Id == messageId &&
                message.ProcessedAtUtc == null && message.DeadAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.AvailableAtUtc, now.Add(effectiveDelay))
                .SetProperty(message => message.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(message => message.LastError, Truncate(reason)), cancellationToken);
        return false;
    }

    public static object DeserializeEvent(OutboxMessage message)
    {
        if (!EventTypes.TryGetValue(message.EventType, out var eventType))
        {
            throw new InvalidOperationException($"Unknown outbox event type '{message.EventType}'.");
        }

        return JsonSerializer.Deserialize(message.Payload, eventType, SerializerOptions)
               ?? throw new InvalidOperationException($"Outbox payload for '{message.EventType}' was empty.");
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
