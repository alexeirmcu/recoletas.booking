using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.ApplicationServices.Messaging;
using Recoletas.Booking.Infrastructure.Clock;
using Recoletas.Booking.Infrastructure.EfModel;
using Recoletas.Booking.Infrastructure.Messaging;
using Recoletas.Booking.Infrastructure.Notifications;
using Recoletas.Booking.Infrastructure.Outbox;
using Recoletas.Booking.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Recoletas.Booking.Infrastructure;

public static class InfrastructureServiceRegistration
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<BookingDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<BookingDatabaseInitializer>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<ISlotRepository, SlotRepository>();
        services.AddScoped<IPatientRepository, PatientRepository>();
        services.AddScoped<IWaitlistRepository, WaitlistRepository>();
        services.AddScoped<IAppointmentRepository, AppointmentRepository>();
        services.AddScoped<IAdminAppointmentReadRepository, AdminAppointmentReadRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddSingleton<IConstraintViolationDetector, PostgresConstraintViolationDetector>();

        services.AddScoped<OutboxStore>();
        services.AddScoped<IOutboxStore>(provider => provider.GetRequiredService<OutboxStore>());

        services.AddScoped<LogNotificationSender>();
        services.AddScoped<WebhookNotificationSender>();
        services.AddScoped<CompositeNotificationSender>(provider => new CompositeNotificationSender(
        [
            provider.GetRequiredService<LogNotificationSender>(),
            provider.GetRequiredService<WebhookNotificationSender>()
        ]));
        services.AddScoped<INotificationSender>(provider => provider.GetRequiredService<CompositeNotificationSender>());

        return services;
    }

    public static IServiceCollection AddRabbitMqMessaging(this IServiceCollection services, RabbitMqOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<IEventDispatcher, RabbitMqDispatcher>();
        services.AddSingleton<RabbitMqDeliveryHandler>();
        services.AddHostedService<RabbitMqConsumersHostedService>();
        return services;
    }
}
