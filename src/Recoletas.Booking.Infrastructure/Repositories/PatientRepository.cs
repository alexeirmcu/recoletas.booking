using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.ApplicationServices.Errors;
using Recoletas.Booking.ApplicationServices.Abstractions;
using Recoletas.Booking.Domain.ValueObjects;
using Recoletas.Booking.Infrastructure.EfModel;
using Recoletas.Booking.Infrastructure.EfModel.EntityConfiguration;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Recoletas.Booking.Infrastructure.Repositories;

public sealed class PatientRepository(BookingDbContext dbContext) : IPatientRepository
{
    public Task<Patient?> GetByIdAsync(PatientId id, CancellationToken cancellationToken) =>
        dbContext.Patients.AsNoTracking().SingleOrDefaultAsync(patient => patient.Id == id, cancellationToken);

    public async Task AddAsync(Patient patient, CancellationToken cancellationToken)
    {
        dbContext.Patients.Add(patient);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsPatientNhcUniqueViolation(exception))
        {
            throw new DuplicatePatientNhcException();
        }
    }

    private static bool IsPatientNhcUniqueViolation(Exception exception)
    {
        var postgresException = FindPostgresException(exception);
        return postgresException?.SqlState == PostgresErrorCodes.UniqueViolation &&
               postgresException.ConstraintName == PatientConfiguration.NhcUniqueIndexName;
    }

    private static PostgresException? FindPostgresException(Exception exception) =>
        exception as PostgresException ??
        (exception.InnerException is { } innerException ? FindPostgresException(innerException) : null);
}
