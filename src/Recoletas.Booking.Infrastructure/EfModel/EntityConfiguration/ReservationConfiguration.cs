using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recoletas.Booking.Domain.Entities;

namespace Recoletas.Booking.Infrastructure.EfModel.EntityConfiguration;

public sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("Reservations");
        builder.HasKey(reservation => reservation.Id);
        builder.Property(reservation => reservation.Id).ValueGeneratedNever();
        builder.Property(reservation => reservation.SlotId).HasConversion(ValueConverters.SlotIdToGuid).IsRequired();
        builder.Property(reservation => reservation.PatientId).HasConversion(ValueConverters.PatientIdToGuid).IsRequired();
        builder.Property(reservation => reservation.ConfirmedAtUtc)
            .HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(reservation => reservation.SlotId).HasDatabaseName("IX_Reservations_SlotId");
        builder.HasIndex(reservation => reservation.PatientId).HasDatabaseName("IX_Reservations_PatientId");
        builder.HasOne<Patient>().WithMany().HasForeignKey(reservation => reservation.PatientId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Slot>().WithMany().HasForeignKey(reservation => reservation.SlotId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
