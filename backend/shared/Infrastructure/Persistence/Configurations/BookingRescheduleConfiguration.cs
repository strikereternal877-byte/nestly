using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class BookingRescheduleConfiguration : IEntityTypeConfiguration<BookingReschedule>
{
    public void Configure(EntityTypeBuilder<BookingReschedule> builder)
    {
        builder.ToTable("booking_reschedule");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.BookingId).IsRequired();
        builder.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.Actor).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Reason).HasMaxLength(500);

        builder.Property(x => x.FromSlotWindowId).IsRequired();
        builder.Property(x => x.FromSlotDate).IsRequired();
        builder.Property(x => x.FromSlotStartTime).IsRequired();
        builder.Property(x => x.FromSlotEndTime).IsRequired();

        builder.Property(x => x.ToSlotWindowId).IsRequired();
        builder.Property(x => x.ToSlotDate).IsRequired();
        builder.Property(x => x.ToSlotStartTime).IsRequired();
        builder.Property(x => x.ToSlotEndTime).IsRequired();

        builder.Property(x => x.IsLate).IsRequired();
        builder.Property(x => x.FeeAmount).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.FeeCollectedAmount).IsRequired().HasPrecision(12, 2).HasDefaultValue(0m);
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasIndex(x => x.BookingId);
    }
}
