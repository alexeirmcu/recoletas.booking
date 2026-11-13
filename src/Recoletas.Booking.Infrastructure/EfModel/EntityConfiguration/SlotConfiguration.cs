using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Infrastructure.EfModel;

namespace Recoletas.Booking.Infrastructure.EfModel.EntityConfiguration;

public sealed class SlotConfiguration : IEntityTypeConfiguration<Slot>
{
    public void Configure(EntityTypeBuilder<Slot> builder)
    {
        builder.ToTable("Slots");
        builder.HasKey(slot => slot.Id);
        builder.Property(slot => slot.Id).HasConversion(ValueConverters.SlotIdToGuid).ValueGeneratedNever();
        builder.Property(slot => slot.TestType).HasConversion(ValueConverters.TestTypeToString).HasMaxLength(100).IsRequired();
        builder.Property(slot => slot.StartsAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(slot => slot.EndsAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(slot => slot.State).HasConversion<int>().IsRequired();
        builder.Property(slot => slot.AssignedPatientId).HasConversion(ValueConverters.PatientIdToGuid);
        builder.Property(slot => slot.CurrentReservationId);
        builder.Property(slot => slot.HoldExpiresAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(slot => slot.OfferExpiresAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(slot => slot.Generation).IsRequired();
        builder.Ignore(slot => slot.DomainEvents);
        builder.HasOne<Patient>().WithMany().HasForeignKey(slot => slot.AssignedPatientId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TestTypeCatalogEntry>().WithMany().HasForeignKey(slot => slot.TestType)
            .HasPrincipalKey(testType => testType.Code).OnDelete(DeleteBehavior.Restrict);
        // Intentionally no FK for CurrentReservationId: Reservations.SlotId -> Slots.Id is the one-way relation.
        builder.HasIndex(slot => slot.CurrentReservationId).IsUnique()
            .HasDatabaseName("IX_Slots_CurrentReservationId")
            .HasFilter("\"CurrentReservationId\" IS NOT NULL");
    }
}
