using Recoletas.Booking.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Recoletas.Booking.Infrastructure.EfModel;

public sealed class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
    public DbSet<Slot> Slots => Set<Slot>();

    public DbSet<Patient> Patients => Set<Patient>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();

    public DbSet<TestTypeCatalogEntry> TestTypes => Set<TestTypeCatalogEntry>();

    public DbSet<OutboxRecord> OutboxMessages => Set<OutboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BookingDbContext).Assembly);
}
