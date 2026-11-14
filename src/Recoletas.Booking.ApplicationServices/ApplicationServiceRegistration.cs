using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Recoletas.Booking.ApplicationServices.Behaviors;
using Recoletas.Booking.ApplicationServices.Commands;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.ApplicationServices.Outbox;
using Recoletas.Booking.ApplicationServices.Validators;
using Recoletas.Booking.ApplicationServices.Workers;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.ApplicationServices.Messaging;

namespace Recoletas.Booking.ApplicationServices;

public sealed class ApplicationServicesOptions
{
    public const string SectionName = "Booking";

    public TimeSpan HoldTtl { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan OfferWindow { get; set; } = TimeSpan.FromMinutes(5);

    public OutboxProcessorOptions Outbox { get; } = new();

    public ExpireHoldsOptions ExpireHolds { get; } = new();
}

public static class ApplicationServiceRegistration
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services,
        ApplicationServicesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);

        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<BlockSlot>();
            configuration.AddOpenBehavior(typeof(LoggingBehavior<,>));
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        services.AddValidatorsFromAssemblyContaining<BlockSlotValidator>();

        services.Configure<OutboxProcessorOptions>(o =>
        {
            o.BatchSize = options.Outbox.BatchSize;
            o.MaxAttempts = options.Outbox.MaxAttempts;
            o.RetryBaseDelay = options.Outbox.RetryBaseDelay;
            o.PollInterval = options.Outbox.PollInterval;
        });
        services.AddScoped(provider => provider.GetRequiredService<IOptions<OutboxProcessorOptions>>().Value);
        services.AddScoped<OutboxProcessor>();
        services.AddScoped<SlotReleasedEventHandler>();
        services.AddScoped<NotificationEventHandler>();

        services.Configure<ExpireHoldsOptions>(o =>
            o.PollInterval = options.ExpireHolds.PollInterval);
        services.AddHostedService<ExpireHoldsHostedService>();
        services.AddHostedService<OutboxProcessorHostedService>();

        return services;
    }
}
