using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recoletas.Booking.Domain.Entities;
using Recoletas.Booking.Infrastructure.EfModel;

namespace Recoletas.Booking.Infrastructure.EfModel.EntityConfiguration;

public sealed class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.ToTable("WaitlistEntries");
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();
        builder.Property(entry => entry.PatientId).HasConversion(ValueConverters.PatientIdToGuid).IsRequired();
        builder.Property(entry => entry.TestType).HasConversion(ValueConverters.TestTypeToString).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.JoinedAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(entry => entry.IsActive).IsRequired();
        builder.HasIndex(entry => new { entry.PatientId, entry.TestType }).IsUnique()
            .HasDatabaseName("IX_WaitlistEntries_OneActivePatientPerTestType")
            .HasFilter("\"IsActive\" = TRUE");
        builder.HasIndex(entry => new { entry.TestType, entry.JoinedAtUtc, entry.Id })
            .HasDatabaseName("IX_WaitlistEntries_Fifo");
        builder.HasOne<Patient>().WithMany().HasForeignKey(entry => entry.PatientId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TestTypeCatalogEntry>().WithMany().HasForeignKey(entry => entry.TestType)
            .HasPrincipalKey(testType => testType.Code).OnDelete(DeleteBehavior.Restrict);
    }
}
