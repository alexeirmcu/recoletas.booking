using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace Recoletas.Booking.API.Auth;

public static class BookingClaimTypes
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string PatientId = "patientId";
    public const string Role = "role";
    public const string PatientRole = "patient";
    public const string AdminRole = "admin";
}

public static class BookingPolicies
{
    public const string AdminOnly = "AdminOnly";
}

public static class BookingAuthenticationExtensions
{
    public static IServiceCollection AddBookingAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var secret = configuration["Jwt:Secret"];
        var issuer = configuration["Jwt:Issuer"];
        var audience = configuration["Jwt:Audience"];

        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
        {
            throw new InvalidOperationException("Jwt:Secret must be configured and contain at least 32 bytes.");
        }

        if (string.IsNullOrWhiteSpace(issuer))
        {
            throw new InvalidOperationException("Jwt:Issuer must be configured.");
        }

        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException("Jwt:Audience must be configured.");
        }

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    NameClaimType = BookingClaimTypes.Subject,
                    RoleClaimType = BookingClaimTypes.Role
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ValidateBookingClaimsAsync,
                    OnChallenge = WriteUnauthorizedResponseAsync
                };
            });

        var patientPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireRole(BookingClaimTypes.PatientRole)
            .RequireClaim(BookingClaimTypes.PatientId)
            .Build();

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = patientPolicy;
            options.AddPolicy(BookingPolicies.AdminOnly, policy =>
                policy.RequireAuthenticatedUser().RequireRole(BookingClaimTypes.AdminRole));
        });

        return services;
    }

    private static Task ValidateBookingClaimsAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var subjects = principal?.FindAll(BookingClaimTypes.Subject).ToArray() ?? [];
        var roles = principal?.FindAll(BookingClaimTypes.Role).Select(claim => claim.Value).ToArray() ?? [];
        var patientIds = principal?.FindAll(BookingClaimTypes.PatientId).ToArray() ?? [];

        if (subjects.Length != 1 || string.IsNullOrWhiteSpace(subjects[0].Value) ||
            roles.Length != 1 || roles[0] is not (BookingClaimTypes.PatientRole or BookingClaimTypes.AdminRole))
        {
            context.Fail("The bearer token has invalid booking identity claims.");
            return Task.CompletedTask;
        }

        if (roles[0] == BookingClaimTypes.PatientRole &&
            (patientIds.Length != 1 ||
             !Guid.TryParse(patientIds[0].Value, out var patientId) ||
             patientId == Guid.Empty))
        {
            context.Fail("A patient bearer token must contain one valid patientId claim.");
            return Task.CompletedTask;
        }

        if (roles[0] == BookingClaimTypes.AdminRole && patientIds.Length != 0)
        {
            context.Fail("An admin bearer token must not contain a patientId claim.");
        }

        return Task.CompletedTask;
    }

    private static async Task WriteUnauthorizedResponseAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers["WWW-Authenticate"] = JwtBearerDefaults.AuthenticationScheme;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            new { error = "unauthorized" },
            context.HttpContext.RequestAborted);
    }
}
