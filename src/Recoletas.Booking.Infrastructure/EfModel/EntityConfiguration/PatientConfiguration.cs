using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recoletas.Booking.Domain.Entities;

namespace Recoletas.Booking.Infrastructure.EfModel.EntityConfiguration;

public sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public const string NhcUniqueIndexName = "IX_Patients_Nhc";

    public void Configure(EntityTypeBuilder<Patient> builder)
    {
        builder.ToTable("Patients");
        builder.HasKey(patient => patient.Id);
        builder.Property(patient => patient.Id).HasConversion(ValueConverters.PatientIdToGuid).ValueGeneratedNever();
        builder.Property(patient => patient.Name).HasMaxLength(200).IsRequired();
        builder.Property(patient => patient.Nhc).HasMaxLength(100);
        builder.HasIndex(patient => patient.Nhc)
            .IsUnique()
            .HasDatabaseName(NhcUniqueIndexName)
            .HasFilter("\"Nhc\" IS NOT NULL");
        builder.Property(patient => patient.CreatedAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(patient => patient.DeletedAtUtc).HasColumnType("timestamp with time zone");
    }
}
