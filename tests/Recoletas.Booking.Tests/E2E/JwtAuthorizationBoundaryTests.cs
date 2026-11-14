using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Contracts.Models;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Tests.TestSupport;

namespace Recoletas.Booking.Tests.E2E;

[TestClass]
[TestCategory("E2E")]
public sealed class JwtAuthorizationBoundaryTests
{
    [TestMethod]
    public async Task Liveness_AnonymousRequest_DoesNotRequireDatabaseOrBroker()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Readiness_DisconnectedBroker_ReturnsUnavailableWithoutExposingDetails()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual("Unhealthy", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task PatientRegistration_AnonymousRequest_IsAllowed()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/patients", new { name = "Test Patient" });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task PatientToken_CanListOwnAppointments()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokens.CreatePatientToken(Guid.NewGuid()));

        var response = await client.GetAsync("/appointments");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            $"{await response.Content.ReadAsStringAsync()} {response.Headers.WwwAuthenticate}");
    }

    [TestMethod]
    public async Task PatientAndAdminTokens_CannotCrossRoleBoundaries()
    {
        using var factory = CreateFactory();
        using var anonymousClient = factory.CreateClient();
        using var patientClient = factory.CreateClient();
        using var adminClient = factory.CreateClient();
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        var adminAppointments = $"/admin/appointments?from={from}&to={to}";
        patientClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokens.CreatePatientToken(Guid.NewGuid()));
        adminClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokens.CreateAdminToken());

        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await anonymousClient.GetAsync("/appointments")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await anonymousClient.GetAsync("/appointments/pending")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await patientClient.GetAsync(adminAppointments)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await adminClient.GetAsync("/appointments")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await adminClient.GetAsync("/appointments/pending")).StatusCode);
    }

    [TestMethod]
    public async Task PendingAppointments_PatientSeesOnlyOwnExactContractAndNoStoreResponse()
    {
        var patientA = Guid.NewGuid();
        var patientB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var startsAt = now.AddHours(1);
        var expiresAt = now.AddMinutes(5);
        var slot = new Slot(SlotId.New(), new TestType("MRI"), startsAt, startsAt.AddMinutes(30));
        Assert.IsTrue(slot.TryHold(new PatientId(patientA), now, expiresAt));
        var slots = new Mock<ISlotRepository>(MockBehavior.Strict);
        slots.Setup(repository => repository.ListPendingForPatientAsync(
                It.Is<PatientId>(id => id.Value == patientA), It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([slot]);
        slots.Setup(repository => repository.ListPendingForPatientAsync(
                It.Is<PatientId>(id => id.Value == patientB), It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        using var factory = CreateFactory(slots.Object);
        using var patientAClient = factory.CreateClient();
        using var patientBClient = factory.CreateClient();
        patientAClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokens.CreatePatientToken(patientA));
        patientBClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokens.CreatePatientToken(patientB));

        var patientAResponse = await patientAClient.GetAsync("/appointments/pending");
        var responseBody = await patientAResponse.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, patientAResponse.StatusCode, responseBody);
        Assert.IsTrue(patientAResponse.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(responseBody);
        var item = document.RootElement[0];
        CollectionAssert.AreEquivalent(
            new[] { "slotId", "testType", "startsAtUtc", "endsAtUtc", "kind", "generation", "expiresAtUtc" },
            item.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(slot.Id.Value, item.GetProperty("slotId").GetGuid());
        Assert.AreEqual("MRI", item.GetProperty("testType").GetString());
        Assert.AreEqual(startsAt, item.GetProperty("startsAtUtc").GetDateTimeOffset());
        Assert.AreEqual(slot.EndsAtUtc, item.GetProperty("endsAtUtc").GetDateTimeOffset());
        Assert.AreEqual("hold", item.GetProperty("kind").GetString());
        Assert.AreEqual(slot.Generation, item.GetProperty("generation").GetInt32());
        Assert.AreEqual(expiresAt, item.GetProperty("expiresAtUtc").GetDateTimeOffset());
        Assert.IsFalse(item.TryGetProperty("patientId", out _));

        var patientBResponse = await patientBClient.GetAsync("/appointments/pending");
        Assert.AreEqual(HttpStatusCode.OK, patientBResponse.StatusCode);
        Assert.IsTrue(patientBResponse.Headers.CacheControl?.NoStore);
        Assert.AreEqual("[]", await patientBResponse.Content.ReadAsStringAsync());
        slots.Verify(repository => repository.ListPendingForPatientAsync(
            It.Is<PatientId>(id => id.Value == patientA), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
        slots.Verify(repository => repository.ListPendingForPatientAsync(
            It.Is<PatientId>(id => id.Value == patientB), It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task AdminToken_CanReadConfirmedAppointments()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokens.CreateAdminToken());
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));

        var response = await client.GetAsync($"/admin/appointments?from={from}&to={to}");
        var appointments = await response.Content.ReadFromJsonAsync<AdminAppointmentSummary[]>();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(appointments);
        Assert.AreEqual(1, appointments.Length);
        Assert.AreEqual("Test Patient", appointments[0].PatientName);
    }

    [TestMethod]
    public async Task AdminAppointments_MissingDateRange_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokens.CreateAdminToken());

        var response = await client.GetAsync("/admin/appointments");
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("from_and_to_required", body?["error"]);
    }

    [TestMethod]
    public async Task AdminAppointments_ToNotAfterFrom_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokens.CreateAdminToken());
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));

        var response = await client.GetAsync($"/admin/appointments?from={from}&to={to}");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task PatientToken_WithoutValidPatientIdClaim_IsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtTokens.CreateToken("patient", "not-a-guid"));

        var response = await client.GetAsync("/appointments");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ExpiredPatientToken_IsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                TestJwtTokens.CreateToken("patient", Guid.NewGuid().ToString(), expired: true));

        var response = await client.GetAsync("/appointments");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task PatientToken_SignedWithDifferentSecret_IsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                TestJwtTokens.CreateToken("patient", Guid.NewGuid().ToString(), signingSecret: "different-test-secret-that-is-long-enough"));

        var response = await client.GetAsync("/appointments");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task PatientToken_WithWrongIssuer_IsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                TestJwtTokens.CreateToken("patient", Guid.NewGuid().ToString(), issuer: "https://wrong-issuer"));

        var response = await client.GetAsync("/appointments");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task PatientToken_WithWrongAudience_IsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                TestJwtTokens.CreateToken("patient", Guid.NewGuid().ToString(), audience: "wrong-audience"));

        var response = await client.GetAsync("/appointments");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(ISlotRepository? slotRepository = null)
    {
        var testConnectionString = PostgresTestDatabase.ConnectionString;
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
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPatientRepository>();
                services.AddSingleton<IPatientRepository>(new InMemoryPatientRepository());
                services.RemoveAll<IAppointmentRepository>();
                services.AddSingleton<IAppointmentRepository>(new EmptyAppointmentRepository());
                services.RemoveAll<IAdminAppointmentReadRepository>();
                services.AddSingleton<IAdminAppointmentReadRepository>(new StaticAdminAppointmentRepository());
                services.RemoveAll<ISlotRepository>();
                services.AddSingleton(slotRepository ?? Mock.Of<ISlotRepository>());
            });
        });
    }

    private sealed class InMemoryPatientRepository : IPatientRepository
    {
        public Task<Patient?> GetByIdAsync(PatientId id, CancellationToken cancellationToken) =>
            Task.FromResult<Patient?>(null);

        public Task AddAsync(Patient patient, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyAppointmentRepository : IAppointmentRepository
    {
        public Task<IReadOnlyList<Reservation>> ListForPatientAsync(
            PatientId patientId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reservation>>([]);
    }

    private sealed class StaticAdminAppointmentRepository : IAdminAppointmentReadRepository
    {
        public Task<IReadOnlyList<AdminAppointmentSummary>> ListConfirmedAsync(
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AdminAppointmentSummary>>(
            [
                new AdminAppointmentSummary(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "BloodTest",
                    fromUtc,
                    fromUtc.AddMinutes(30),
                    fromUtc,
                    Guid.NewGuid(),
                    "Test Patient")
            ]);
    }
}
