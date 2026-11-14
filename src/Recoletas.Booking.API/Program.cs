using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Recoletas.Booking.API.Auth;
using Recoletas.Booking.API.Middleware;
using Recoletas.Booking.ApplicationServices;
using Recoletas.Booking.Infrastructure;
using Recoletas.Booking.Infrastructure.EfModel;
using Recoletas.Booking.Infrastructure.Health;
using Recoletas.Booking.Infrastructure.Messaging;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();
var logDirectory = Path.Combine(builder.Environment.ContentRootPath, "logs");
builder.Services.AddSerilog((_, loggerConfiguration) =>
{
    loggerConfiguration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console();

    if (isDevelopment)
    {
        Directory.CreateDirectory(logDirectory);
        loggerConfiguration.WriteTo.File(
            Path.Combine(logDirectory, "recoletas-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 10 * 1024 * 1024,
            rollOnFileSizeLimit: true);
    }
});

var connectionString = builder.Configuration.GetConnectionString("Booking");
if (builder.Environment.IsEnvironment("Testing") && string.IsNullOrWhiteSpace(connectionString))
{
    connectionString = "Host=localhost;Database=booking_tests;Username=test;Password=test";
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'ConnectionStrings:Booking' is not configured. Set the 'ConnectionStrings__Booking' environment variable.");
}

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPatientIdentityResolver, PatientIdentityResolver>();
builder.Services.AddBookingAuthentication(builder.Configuration);
var bookingOptions = builder.Configuration.GetSection(ApplicationServicesOptions.SectionName)
    .Get<ApplicationServicesOptions>() ?? new ApplicationServicesOptions();
builder.Services.AddApplicationServices(bookingOptions);
builder.Services.AddInfrastructure(connectionString);
var rabbitMqOptions = builder.Configuration.GetSection(RabbitMqOptions.SectionName)
    .Get<RabbitMqOptions>() ?? new RabbitMqOptions();
if (!builder.Environment.IsEnvironment("Testing"))
{
    rabbitMqOptions.Validate();
}

builder.Services.AddRabbitMqMessaging(rabbitMqOptions);
builder.Services.AddHealthChecks()
    .AddCheck<BookingReadinessHealthCheck>("booking", tags: ["ready"],
        timeout: TimeSpan.FromSeconds(5));
if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.RemoveAll<IHostedService>();
}

var app = builder.Build();
if (args.Contains("--migrate-only", StringComparer.Ordinal))
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    await migrationScope.ServiceProvider.GetRequiredService<BookingDatabaseInitializer>()
        .InitializeAsync(seedDemoData: false);
    return;
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseAuthentication();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options =>
        options.SwaggerEndpoint("/openapi.yaml", "Recoletas.Booking API v1"));
    app.MapGet("/openapi.yaml", () =>
        Results.File(Path.Combine(AppContext.BaseDirectory, "openapi.yaml"), "application/yaml"))
        .AllowAnonymous();
}

app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();

if (!app.Environment.IsEnvironment("Testing") &&
    app.Configuration.GetValue("Booking:ApplyMigrationsOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var initializer = scope.ServiceProvider.GetRequiredService<BookingDatabaseInitializer>();
    var seedDemoData = app.Environment.IsDevelopment() &&
                       app.Configuration.GetValue<bool>("Booking:SeedDemoData");
    await initializer.InitializeAsync(seedDemoData);
}

await app.RunAsync();

public partial class Program { }
