# JWT Authentication Guide

The Booking API validates JWT bearer tokens; it does not issue tokens or provide a login flow. This
demo uses HS256 with a shared signing secret. The API validates the signature, issuer, audience,
expiration, and HS256 algorithm. The signing secret must contain at least 32 bytes.

## Configure local development

The API project has a User Secrets ID. Set a private, random secret of at least 32 bytes, plus the
issuer and audience values used in the token:

```powershell
dotnet user-secrets set "Jwt:Secret" "<replace-with-a-private-random-secret-of-32-or-more-bytes>" --project src\Recoletas.Booking.API
dotnet user-secrets set "Jwt:Issuer" "recoletas-booking-demo" --project src\Recoletas.Booking.API
dotnet user-secrets set "Jwt:Audience" "recoletas-booking-api" --project src\Recoletas.Booking.API
dotnet user-secrets list --project src\Recoletas.Booking.API
```

Replace the secret placeholder before running the command. User Secrets are loaded automatically in
Development and are stored outside the repository. Do not add a real secret to `appsettings.json` or
commit it. Other environments must provide the same values through a secret store or the
`Jwt__Secret`, `Jwt__Issuer`, and `Jwt__Audience` environment variables.

## Create a patient token with jwt.io

1. Start PostgreSQL and the API, then use Swagger to call `POST /patients` without authentication.
   Save the returned `patientId`; the patient must already exist in the demo database.
2. Get a fresh 15-minute expiration timestamp in PowerShell:

   ```powershell
   [DateTimeOffset]::UtcNow.AddMinutes(15).ToUnixTimeSeconds()
   ```

3. Open [jwt.io](https://jwt.io). In **PAYLOAD: DATA**, enter the following claims, replacing the
   patient ID and `exp` with the values from the previous step:

   ```json
   {
     "sub": "demo-user-1",
     "role": "patient",
     "patientId": "REPLACE_WITH_PATIENT_ID",
     "iss": "recoletas-booking-demo",
     "aud": "recoletas-booking-api",
     "exp": 1798761600
   }
   ```

4. In **VERIFY SIGNATURE**, select `HS256` and paste the local `Jwt:Secret` value shown by
   `dotnet user-secrets list`. Confirm the signature verifies, then copy the encoded token.
5. Paste the token into Swagger's **Authorize** dialog. Protected patient endpoints should accept
   it. Missing, expired, incorrectly signed, or issuer/audience-mismatched tokens return `401`.

The patient's `patientId` claim must be the GUID returned by `POST /patients`; `sub` can be any
non-empty subject for this demo. Admin tokens use `role: admin` and must omit `patientId`.

## Security note

Anyone who knows the shared secret can create tokens, including admin tokens. Use jwt.io only with a
throwaway local demo secret and synthetic data; never enter a production secret there. This setup is
for demonstration only, not a production identity-provider integration.
