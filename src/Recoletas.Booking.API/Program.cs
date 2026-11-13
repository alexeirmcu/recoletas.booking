using Microsoft.Extensions.Configuration;
using Recoletas.Booking.API.Auth;
using Recoletas.Booking.API.Middleware;
using Recoletas.Booking.ApplicationServices;
using Recoletas.Booking.Infrastructure;
using Recoletas.Booking.Infrastructure.EfModel;
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
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'ConnectionStrings:Booking' is not configured. Set the 'ConnectionStrings__Booking' environment variable.");
}

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPatientIdentityResolver, PatientIdentityResolver>();
var bookingOptions = builder.Configuration.GetSection(ApplicationServicesOptions.SectionName)
    .Get<ApplicationServicesOptions>() ?? new ApplicationServicesOptions();
builder.Services.AddApplicationServices(bookingOptions);
builder.Services.AddInfrastructure(connectionString);

var app = builder.Build();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<PatientIdentityMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

await using (var scope = app.Services.CreateAsyncScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<BookingDatabaseInitializer>();
    var seedDemoData = app.Environment.IsDevelopment() &&
                       app.Configuration.GetValue<bool>("Booking:SeedDemoData");
    await initializer.InitializeAsync(seedDemoData);
}

await app.RunAsync();

public partial class Program { }
