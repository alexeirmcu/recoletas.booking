using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Contracts.Events;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.ApplicationServices.Handlers;
using Recoletas.Booking.Infrastructure.EfModel;
using Recoletas.Booking.Infrastructure.Messaging;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.E2E;

[TestClass]
[TestCategory("E2E")]
public sealed class BookingFlowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task HoldThenConfirm_AuthenticatedPatient_ReceivesConfirmedAppointment()
    {
        var connectionString = PostgresTestDatabase.CreateScenarioConnectionString("holdconfirm");
        await PostgresTestDatabase.EnsureAvailableAsync(connectionString);
        using var factory = CreateFactory(connectionString);
        using var client = factory.CreateClient();
        var patientId = await RegisterPatientAsync(client);
        var slotId = await AddFutureSlotAsync(factory);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestJwtTokens.CreatePatientToken(patientId));

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
        var appointmentsResponse = await client.GetAsync("/appointments");
        appointmentsResponse.EnsureSuccessStatusCode();
        var appointments = await appointmentsResponse.Content.ReadFromJsonAsync<AppointmentSummary[]>(JsonOptions);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        var slot = await context.Slots.AsNoTracking().SingleAsync(candidate => candidate.Id == new SlotId(slotId));

        Assert.IsNotNull(appointment);
        Assert.AreEqual(slotId, appointment.SlotId);
        Assert.AreEqual(patientId, appointment.PatientId);
        Assert.AreEqual("BloodTest", appointment.TestType);
        Assert.AreEqual(slot.StartsAtUtc, appointment.StartsAtUtc);
        Assert.AreEqual(slot.EndsAtUtc, appointment.EndsAtUtc);
        Assert.IsNotNull(appointments);
        Assert.AreEqual(1, appointments.Length);
        Assert.AreEqual(appointment.ReservationId, appointments[0].ReservationId);
        Assert.AreEqual(slot.StartsAtUtc, appointments[0].StartsAtUtc);
        Assert.AreEqual(slot.EndsAtUtc, appointments[0].EndsAtUtc);
    }

    [TestMethod]
    public async Task PendingHold_ReadGenerationAndConfirm_RemovesItemFromPendingResults()
    {
        var connectionString = PostgresTestDatabase.CreateScenarioConnectionString("pendingflow");
        await PostgresTestDatabase.EnsureAvailableAsync(connectionString);
        using var factory = CreateFactory(connectionString);
        using var client = factory.CreateClient();
        var patientId = await RegisterPatientAsync(client);
        var slotId = await AddFutureSlotAsync(factory);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestJwtTokens.CreatePatientToken(patientId));

        var holdResponse = await client.PostAsync($"/slots/{slotId}/hold", content: null);
        holdResponse.EnsureSuccessStatusCode();
        var hold = await holdResponse.Content.ReadFromJsonAsync<BookingResult>(JsonOptions);
        Assert.IsNotNull(hold?.Generation);

        var pendingResponse = await client.GetAsync("/appointments/pending");
        pendingResponse.EnsureSuccessStatusCode();
        var pending = await pendingResponse.Content.ReadFromJsonAsync<PendingAppointmentSummary[]>(JsonOptions);
        Assert.IsNotNull(pending);
        Assert.AreEqual(1, pending.Length);
        Assert.AreEqual(slotId, pending[0].SlotId);
        Assert.AreEqual("hold", pending[0].Kind);
        Assert.AreEqual(hold.Generation.Value, pending[0].Generation);

        var confirmResponse = await client.PostAsJsonAsync($"/holds/{slotId}/confirm",
            new { generation = pending[0].Generation }, JsonOptions);
        confirmResponse.EnsureSuccessStatusCode();
        var afterConfirmation = await client.GetAsync("/appointments/pending");
        afterConfirmation.EnsureSuccessStatusCode();

        Assert.AreEqual("[]", await afterConfirmation.Content.ReadAsStringAsync());
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
        var connectionString = PostgresTestDatabase.CreateScenarioConnectionString("waitliste2e");
        await PostgresTestDatabase.EnsureAvailableAsync(connectionString);
        using var factory = CreateFactory(connectionString);
        using var ownerClient = factory.CreateClient();
        using var waiterClient = factory.CreateClient();
        var ownerId = await RegisterPatientAsync(ownerClient);
        var waiterId = await RegisterPatientAsync(waiterClient);
        var slotId = await AddFutureSlotAsync(factory);

        ownerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestJwtTokens.CreatePatientToken(ownerId));
        var holdResponse = await ownerClient.PostAsync($"/slots/{slotId}/hold", content: null);
        holdResponse.EnsureSuccessStatusCode();
        var hold = await holdResponse.Content.ReadFromJsonAsync<BookingResult>(JsonOptions);
        Assert.IsNotNull(hold?.Generation);
        var confirmResponse = await ownerClient.PostAsJsonAsync($"/holds/{slotId}/confirm",
            new { generation = hold.Generation.Value }, JsonOptions);
        confirmResponse.EnsureSuccessStatusCode();
        var ownerAppointment = await confirmResponse.Content.ReadFromJsonAsync<AppointmentSummary>(JsonOptions);
        Assert.IsNotNull(ownerAppointment);

        waiterClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestJwtTokens.CreatePatientToken(waiterId));
        var joinResponse = await waiterClient.PostAsJsonAsync("/waitlist", new { testType = "BloodTest" },
            JsonOptions);
        joinResponse.EnsureSuccessStatusCode();

        var cancelResponse = await ownerClient.PostAsync(
            $"/appointments/{ownerAppointment.ReservationId}/cancel", content: null);
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, cancelResponse.StatusCode);
        await DispatchSlotReleasedEventAsync(factory, slotId);
        var offerGeneration = await WaitForOfferAsync(factory, slotId, waiterId);

        var pendingResponse = await waiterClient.GetAsync("/appointments/pending");
        pendingResponse.EnsureSuccessStatusCode();
        var pending = await pendingResponse.Content.ReadFromJsonAsync<PendingAppointmentSummary[]>(JsonOptions);
        Assert.IsNotNull(pending);
        var offeredSlot = pending.Single(item => item.SlotId == slotId);
        Assert.AreEqual("offer", offeredSlot.Kind);
        Assert.AreEqual(offerGeneration, offeredSlot.Generation);

        var acceptResponse = await waiterClient.PostAsJsonAsync($"/offers/{slotId}/accept",
            new { generation = offeredSlot.Generation }, JsonOptions);
        acceptResponse.EnsureSuccessStatusCode();
        var waiterAppointment = await acceptResponse.Content.ReadFromJsonAsync<AppointmentSummary>(JsonOptions);
        var afterAcceptanceResponse = await waiterClient.GetAsync("/appointments/pending");
        afterAcceptanceResponse.EnsureSuccessStatusCode();
        var afterAcceptance = await afterAcceptanceResponse.Content
            .ReadFromJsonAsync<PendingAppointmentSummary[]>(JsonOptions);

        Assert.IsNotNull(waiterAppointment);
        Assert.AreEqual(slotId, waiterAppointment.SlotId);
        Assert.AreEqual(waiterId, waiterAppointment.PatientId);
        Assert.AreNotEqual(ownerAppointment.ReservationId, waiterAppointment.ReservationId);
        Assert.IsNotNull(afterAcceptance);
        Assert.IsFalse(afterAcceptance.Any(item => item.SlotId == slotId));
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return CreateFactory(PostgresTestDatabase.ConnectionString);
    }

    private static WebApplicationFactory<Program> CreateFactory(string testConnectionString)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Jwt:Secret", TestJwtTokens.Secret);
            builder.UseSetting("Jwt:Issuer", TestJwtTokens.Issuer);
            builder.UseSetting("Jwt:Audience", TestJwtTokens.Audience);
            builder.UseSetting("ConnectionStrings:Booking", testConnectionString);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Booking"] = testConnectionString,
                    ["Jwt:Secret"] = TestJwtTokens.Secret,
                    ["Jwt:Issuer"] = TestJwtTokens.Issuer,
                    ["Jwt:Audience"] = TestJwtTokens.Audience
                }));
        });
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

    private static async Task<int> WaitForOfferAsync(WebApplicationFactory<Program> factory, Guid slotId,
        Guid expectedPatientId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
            var slot = await context.Slots.AsNoTracking().SingleAsync(candidate => candidate.Id == new SlotId(slotId));
            if (slot.State == SlotState.Offered)
            {
                Assert.AreEqual(new PatientId(expectedPatientId), slot.AssignedPatientId,
                    "The released slot was offered to a different waitlist patient.");
                return slot.Generation;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.Fail("The released slot was not offered to the waitlisted patient before the timeout.");
        return 0;
    }

    private static async Task DispatchSlotReleasedEventAsync(WebApplicationFactory<Program> factory, Guid slotId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        var record = await context.OutboxMessages.AsNoTracking().SingleAsync(message =>
            message.EventType == BookingEventTypes.SlotReleased &&
            message.Payload.Contains(slotId.ToString()));
        var message = new OutboxMessage(record.Id, record.EventType, record.Payload, record.OccurredAtUtc);
        if (BookingEventSerializer.Deserialize(message) is not SlotReleased released)
        {
            Assert.Fail("The slot release outbox record did not deserialize to SlotReleased.");
            return;
        }

        await scope.ServiceProvider.GetRequiredService<SlotReleasedEventHandler>()
            .HandleAsync(released, CancellationToken.None);
    }
}
