using Recoletas.Booking.Domain.Repository;
using Recoletas.Booking.Domain.Services;
using Recoletas.Booking.Infrastructure.Clock;
using Recoletas.Booking.Infrastructure.EfModel;
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
}
