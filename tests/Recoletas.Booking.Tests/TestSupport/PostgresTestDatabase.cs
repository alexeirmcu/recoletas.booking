using Microsoft.EntityFrameworkCore;
using Npgsql;
using Recoletas.Booking.Infrastructure.EfModel;
using System.Net.Sockets;

namespace Recoletas.Booking.Tests.TestSupport;

public static class PostgresTestDatabase
{
    private const string DefaultBookingConnectionString =
        "Host=localhost;Port=5433;Database=recoletas;Username=postgres;Password=postgres";
    private const string DefaultTestDatabaseName = "recoletas_tests";
    private const long MigrationLockId = 0x5245434F4C455441;
    private static readonly SemaphoreSlim InitializationLock = new(1, 1);

    public static string ConnectionString
    {
        get
        {
            var testConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__BookingTests");
            var bookingConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Booking");
            var sourceConnectionString = string.IsNullOrWhiteSpace(testConnectionString)
                ? string.IsNullOrWhiteSpace(bookingConnectionString)
                    ? DefaultBookingConnectionString
                    : bookingConnectionString
                : testConnectionString;
            var builder = new NpgsqlConnectionStringBuilder(sourceConnectionString);

            if (string.IsNullOrWhiteSpace(testConnectionString))
            {
                builder.Database = DefaultTestDatabaseName;
            }
            else if (string.IsNullOrWhiteSpace(builder.Database))
            {
                builder.Database = DefaultTestDatabaseName;
            }

            if (string.Equals(builder.Database, "recoletas", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings__BookingTests must target a dedicated test database, not 'recoletas'.");
            }

            return builder.ConnectionString;
        }
    }

    public static DbContextOptions<BookingDbContext> CreateOptions() =>
        CreateOptions(ConnectionString);

    public static DbContextOptions<BookingDbContext> CreateOptions(string connectionString)
    {
        ValidateDedicatedConnectionString(connectionString);
        return new DbContextOptionsBuilder<BookingDbContext>().UseNpgsql(connectionString).Options;
    }

    public static string CreateScenarioConnectionString(string scenarioName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
        if (scenarioName.Length > 15 || scenarioName.Any(character =>
                character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')))
        {
            throw new ArgumentException("Scenario names must be lowercase alphanumeric and at most 15 characters.",
                nameof(scenarioName));
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = $"bookingtest_{scenarioName}_{Guid.NewGuid():N}"
        };
        return builder.ConnectionString;
    }

    public static Task EnsureAvailableAsync() => EnsureAvailableAsync(ConnectionString);

    public static async Task EnsureAvailableAsync(string connectionString)
    {
        ValidateDedicatedConnectionString(connectionString);
        var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);

        await InitializationLock.WaitAsync();
        try
        {
            await EnsureDatabaseExistsAsync(targetBuilder);

            await using var context = new BookingDbContext(
                new DbContextOptionsBuilder<BookingDbContext>().UseNpgsql(connectionString).Options);
            await MigrateWithAdvisoryLockAsync(context);
            await new BookingDatabaseInitializer(context, new FakeClock(DateTimeOffset.UtcNow))
                .InitializeAsync(seedDemoData: false);
        }
        catch (Exception exception) when (IsPostgresUnavailable(exception))
        {
            Assert.Inconclusive($"PostgreSQL integration tests could not initialize the database: {exception.Message}");
        }
        finally
        {
            InitializationLock.Release();
        }
    }

    private static async Task EnsureDatabaseExistsAsync(NpgsqlConnectionStringBuilder targetBuilder)
    {
        var databaseName = string.IsNullOrWhiteSpace(targetBuilder.Database)
            ? throw new InvalidOperationException("The PostgreSQL test database name is required.")
            : targetBuilder.Database;

        await using (var targetConnection = new NpgsqlConnection(targetBuilder.ConnectionString))
        {
            try
            {
                await targetConnection.OpenAsync();
                return;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
            {
                // Connect to the maintenance database below to create the isolated test database.
            }
        }

        var adminBuilder = new NpgsqlConnectionStringBuilder(targetBuilder.ConnectionString)
        {
            Database = "postgres"
        };
        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();

        await using var existsCommand = adminConnection.CreateCommand();
        existsCommand.CommandText = "SELECT 1 FROM pg_database WHERE datname = @databaseName";
        existsCommand.Parameters.AddWithValue("databaseName", databaseName);
        if (await existsCommand.ExecuteScalarAsync() is not null)
        {
            return;
        }

        await using var createCommand = adminConnection.CreateCommand();
        createCommand.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)}";
        try
        {
            await createCommand.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // Another test process created the database after the existence check.
        }
    }

    private static async Task MigrateWithAdvisoryLockAsync(BookingDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        var connection = context.Database.GetDbConnection();
        var lockAcquired = false;
        try
        {
            await using (var lockCommand = connection.CreateCommand())
            {
                lockCommand.CommandText = "SELECT pg_advisory_lock(@lockId)";
                var lockParameter = lockCommand.CreateParameter();
                lockParameter.ParameterName = "lockId";
                lockParameter.Value = MigrationLockId;
                lockCommand.Parameters.Add(lockParameter);
                await lockCommand.ExecuteNonQueryAsync();
                lockAcquired = true;
            }

            await context.Database.MigrateAsync();
        }
        finally
        {
            if (lockAcquired)
            {
                await using var unlockCommand = connection.CreateCommand();
                unlockCommand.CommandText = "SELECT pg_advisory_unlock(@lockId)";
                var lockParameter = unlockCommand.CreateParameter();
                lockParameter.ParameterName = "lockId";
                lockParameter.Value = MigrationLockId;
                unlockCommand.Parameters.Add(lockParameter);
                await unlockCommand.ExecuteNonQueryAsync();
            }

            await context.Database.CloseConnectionAsync();
        }
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static void ValidateDedicatedConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.Equals(builder.Database, "recoletas", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PostgreSQL tests must target a dedicated test database, not 'recoletas'.");
        }
    }

    private static bool IsPostgresUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SocketException or TimeoutException or IOException)
            {
                return true;
            }

            if (current is PostgresException postgresException &&
                (postgresException.SqlState.StartsWith("08", StringComparison.Ordinal) ||
                 postgresException.SqlState is PostgresErrorCodes.AdminShutdown or
                     PostgresErrorCodes.CrashShutdown or PostgresErrorCodes.CannotConnectNow or
                     PostgresErrorCodes.TooManyConnections))
            {
                return true;
            }
        }

        return false;
    }
}
