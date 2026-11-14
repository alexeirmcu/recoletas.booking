using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Recoletas.Booking.Infrastructure.EfModel;

namespace Recoletas.Booking.Infrastructure.EfModel.EntityConfiguration;

public sealed class OutboxRecordConfiguration : IEntityTypeConfiguration<OutboxRecord>
{
    public void Configure(EntityTypeBuilder<OutboxRecord> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).ValueGeneratedNever();
        builder.Property(message => message.EventType).HasMaxLength(100).IsRequired();
        builder.Property(message => message.Payload).IsRequired();
        builder.Property(message => message.OccurredAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(message => message.AvailableAtUtc).HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(message => message.LeaseUntilUtc).HasColumnType("timestamp with time zone");
        builder.Property(message => message.LeaseToken);
        builder.Property(message => message.ProcessedAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(message => message.DeadAtUtc).HasColumnType("timestamp with time zone");
        builder.Property(message => message.Attempts).IsRequired();
        builder.Property(message => message.LastError).HasMaxLength(2000);
        builder.HasIndex(message => new
            { message.ProcessedAtUtc, message.DeadAtUtc, message.AvailableAtUtc })
            .HasDatabaseName("IX_OutboxMessages_Ready");
    }
}
