using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Recoletas.Booking.Infrastructure.EfModel.EntityConfiguration;

public sealed class TestTypeCatalogConfiguration : IEntityTypeConfiguration<TestTypeCatalogEntry>
{
    public void Configure(EntityTypeBuilder<TestTypeCatalogEntry> builder)
    {
        builder.ToTable("TestTypes");
        builder.HasKey(testType => testType.Code);
        builder.Property(testType => testType.Code)
            .HasConversion(ValueConverters.TestTypeToString)
            .HasColumnName("Code")
            .HasMaxLength(100)
            .ValueGeneratedNever();
    }
}
