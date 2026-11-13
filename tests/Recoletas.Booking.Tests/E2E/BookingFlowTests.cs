using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.EfModel;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.E2E;

[TestClass]
[TestCategory("E2E")]
[DoNotParallelize]
public sealed class BookingFlowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task HoldThenConfirm_AuthenticatedPatient_ReceivesConfirmedAppointment()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var patientId = await RegisterPatientAsync(client);
        var slotId = await AddFutureSlotAsync(factory);
        client.DefaultRequestHeaders.Add("X-Patient-Id", patientId.ToString());

        var holdResponse = await client.PostAsync($"/slots/{slotId}/hold", content: null);
        holdResponse.EnsureSuccessStatusCode();
        var holdResult = await holdResponse.Content.ReadFromJsonAsync<BookingResult>(JsonOptions);
        Assert.IsNotNull(holdResult);
        Assert.IsTrue(holdResult.Succeeded);
        Assert.IsNotNull(holdResult.Generation);

        var confirmResponse = await client.PostAsJsonAsync($"/holds/{slotId}/confirm",
            new { generation = holdResult.Generation.Value }, JsonOptions);
        confirmResponse.EnsureSuccessStatusCode();
        var appointment = await confirmResponse.Content.ReadFromJsonAsync<AppointmentSummary>(JsonOptions);

        Assert.IsNotNull(appointment);
        Assert.AreEqual(slotId, appointment.SlotId);
        Assert.AreEqual(patientId, appointment.PatientId);
        Assert.AreEqual("BloodTest", appointment.TestType);
    }

    [TestMethod]
    public async Task RegisterPatient_WhenNhcAlreadyExists_ReturnsStableConflict()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var nhc = $"NHC-{Guid.NewGuid():N}";

        var firstResponse = await client.PostAsJsonAsync("/patients",
            new { name = "First NHC patient", nhc = $"  {nhc} " }, JsonOptions);
        firstResponse.EnsureSuccessStatusCode();
        var firstPatient = await firstResponse.Content.ReadFromJsonAsync<PatientSummary>(JsonOptions);

        var duplicateResponse = await client.PostAsJsonAsync("/patients",
            new { name = "Duplicate NHC patient", nhc }, JsonOptions);
        var errorBody = await duplicateResponse.Content.ReadAsStringAsync();

        Assert.AreEqual(nhc, firstPatient?.Nhc);
        Assert.AreEqual(System.Net.HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        using var errorDocument = JsonDocument.Parse(errorBody);
        Assert.AreEqual("patient_nhc_already_exists", errorDocument.RootElement.GetProperty("error").GetString());

        await using var context = new BookingDbContext(PostgresTestDatabase.CreateOptions());
        Assert.AreEqual(1, await context.Patients.CountAsync(patient => patient.Nhc == nhc));
    }

    [TestMethod]
    public async Task WaitlistReleaseOfferAccept_WaiterReceivesAppointment()
    {
        await PostgresTestDatabase.EnsureAvailableAsync();
        using var factory = CreateFactory();
        using var ownerClient = factory.CreateClient();
        using var waiterClient = factory.CreateClient();
        var ownerId = await RegisterPatientAsync(ownerClient);
        var waiterId = await RegisterPatientAsync(waiterClient);
        var slotId = await AddFutureSlotAsync(factory);

        ownerClient.DefaultRequestHeaders.Add("X-Patient-Id", ownerId.ToString());
        var holdResponse = await ownerClient.PostAsync($"/slots/{slotId}/hold", content: null);
        holdResponse.EnsureSuccessStatusCode();
        var hold = await holdResponse.Content.ReadFromJsonAsync<BookingResult>(JsonOptions);
        Assert.IsNotNull(hold?.Generation);
        var confirmResponse = await ownerClient.PostAsJsonAsync($"/holds/{slotId}/confirm",
            new { generation = hold.Generation.Value }, JsonOptions);
        confirmResponse.EnsureSuccessStatusCode();
        var ownerAppointment = await confirmResponse.Content.ReadFromJsonAsync<AppointmentSummary>(JsonOptions);
        Assert.IsNotNull(ownerAppointment);

        waiterClient.DefaultRequestHeaders.Add("X-Patient-Id", waiterId.ToString());
        var joinResponse = await waiterClient.PostAsJsonAsync("/waitlist", new { testType = "BloodTest" },
            JsonOptions);
        joinResponse.EnsureSuccessStatusCode();

        var cancelResponse = await ownerClient.PostAsync(
            $"/appointments/{ownerAppointment.ReservationId}/cancel", content: null);
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, cancelResponse.StatusCode);
        var offerGeneration = await WaitForOfferAsync(factory, slotId);

        var acceptResponse = await waiterClient.PostAsJsonAsync($"/offers/{slotId}/accept",
            new { generation = offerGeneration }, JsonOptions);
        acceptResponse.EnsureSuccessStatusCode();
        var waiterAppointment = await acceptResponse.Content.ReadFromJsonAsync<AppointmentSummary>(JsonOptions);

        Assert.IsNotNull(waiterAppointment);
        Assert.AreEqual(slotId, waiterAppointment.SlotId);
        Assert.AreEqual(waiterId, waiterAppointment.PatientId);
        Assert.AreNotEqual(ownerAppointment.ReservationId, waiterAppointment.ReservationId);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var testConnectionString = PostgresTestDatabase.ConnectionString;
        var previousConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Booking");
        WebApplicationFactory<Program>? factory = null;
        Environment.SetEnvironmentVariable("ConnectionStrings__Booking", testConnectionString);
        try
        {
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Booking"] = testConnectionString
                    }));
            });

            // Force host startup while the bootstrap environment variable is set, then restore it.
            _ = factory.Services;
            return factory;
        }
        catch
        {
            factory?.Dispose();
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Booking", previousConnectionString);
        }
    }

    private static async Task<Guid> RegisterPatientAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/patients", new { name = "Test Patient" });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("patientId").GetGuid();
    }

    private static async Task<Guid> AddFutureSlotAsync(WebApplicationFactory<Program> factory)
    {
        var slotId = SlotId.New();
        var startsAt = DateTimeOffset.UtcNow.AddDays(4);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        context.Slots.Add(new Slot(slotId, new TestType("BloodTest"), startsAt, startsAt.AddMinutes(30)));
        await context.SaveChangesAsync();
        return slotId.Value;
    }

    private static async Task<int> WaitForOfferAsync(WebApplicationFactory<Program> factory, Guid slotId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
            var slot = await context.Slots.AsNoTracking().SingleAsync(candidate => candidate.Id == new SlotId(slotId));
            if (slot.State == SlotState.Offered)
            {
                return slot.Generation;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail("The released slot was not offered to the waitlisted patient before the timeout.");
        return 0;
    }
}
