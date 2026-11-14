using System.Diagnostics;
using Npgsql;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.E2E;

[TestClass]
[TestCategory("E2E")]
public sealed class MigrationModeTests
{
    [TestMethod]
    public async Task Run_MigrationOnly_MigratesAndExitsWithoutWorkersOrDemoSlots()
    {
        var connection = new NpgsqlConnectionStringBuilder(PostgresTestDatabase.ConnectionString);
        var databaseName = $"bookingmigration_{Guid.NewGuid():N}";
        connection.Database = "postgres";
        await using (var admin = new NpgsqlConnection(connection.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        connection.Database = databaseName;
        var start = CreateStartInfo(connection.ConnectionString);

        var result = await RunMigrationAsync(start);

        Assert.AreEqual(0, result.ExitCode, result.Output);
        await using var verify = new NpgsqlConnection(connection.ConnectionString);
        await verify.OpenAsync();
        await using var counts = new NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM "__EFMigrationsHistory"),
                (SELECT count(*) FROM "TestTypes"),
                (SELECT count(*) FROM "Slots");
            """, verify);
        await using var reader = await counts.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.IsTrue(reader.GetInt64(0) > 0);
        Assert.AreEqual(3L, reader.GetInt64(1));
        Assert.AreEqual(0L, reader.GetInt64(2));
    }

    [TestMethod]
    public async Task Run_MigrationOnly_InvalidDatabaseCredentials_ExitsWithFailure()
    {
        var connection = new NpgsqlConnectionStringBuilder(PostgresTestDatabase.ConnectionString)
        {
            Username = $"invalid_migration_{Guid.NewGuid():N}",
            Password = "not-a-real-test-credential",
            Timeout = 3
        };
        var start = CreateStartInfo(connection.ConnectionString);

        var result = await RunMigrationAsync(start);

        Assert.AreNotEqual(0, result.ExitCode, "Migration failure must not be reported as success.");
    }

    private static ProcessStartInfo CreateStartInfo(string connectionString)
    {
        var testAssembly = typeof(MigrationModeTests).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in new[]
        {
            "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
            typeof(Program).Assembly.Location, "--migrate-only"
        })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["ConnectionStrings__Booking"] = connectionString;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["Booking__ApplyMigrationsOnStartup"] = "false";
        start.Environment["Booking__SeedDemoData"] = "true";
        start.Environment["Jwt__Secret"] = TestJwtTokens.Secret;
        start.Environment["Jwt__Issuer"] = TestJwtTokens.Issuer;
        start.Environment["Jwt__Audience"] = TestJwtTokens.Audience;
        start.Environment["RabbitMq__HostName"] = "127.0.0.1";
        start.Environment["RabbitMq__Port"] = "1";
        start.Environment["RabbitMq__UserName"] = "migration-test";
        start.Environment["RabbitMq__Password"] = "migration-test";
        return start;
    }

    private static async Task<(int ExitCode, string Output)> RunMigrationAsync(ProcessStartInfo start)
    {
        using var process = new Process { StartInfo = start };
        Assert.IsTrue(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail("Migration-only mode did not exit within 45 seconds.");
        }

        return (process.ExitCode, $"{await output}{await error}");
    }
}
