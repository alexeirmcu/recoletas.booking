using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Recoletas.Booking.Tests.TestSupport;

internal static class TestJwtTokens
{
    internal const string Secret = "test-signing-key-must-be-at-least-32-bytes";
    internal const string Issuer = "https://booking-test-issuer";
    internal const string Audience = "booking-test-api";
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes(Secret));

    public static string CreatePatientToken(Guid patientId) =>
        CreateToken("patient", patientId.ToString());

    public static string CreateAdminToken() => CreateToken("admin");

    public static string CreateToken(
        string role,
        string? patientId = null,
        bool expired = false,
        string? issuer = null,
        string? audience = null,
        string? signingSecret = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, $"test-user-{Guid.NewGuid():N}"),
            new("role", role)
        };
        if (patientId is not null)
        {
            claims.Add(new Claim("patientId", patientId));
        }

        var signingKey = signingSecret is null
            ? SigningKey
            : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingSecret));
        var token = new JwtSecurityToken(
            issuer ?? Issuer,
            audience ?? Audience,
            claims,
            now.AddMinutes(expired ? -60 : -10),
            now.AddMinutes(expired ? -30 : 5),
            new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
