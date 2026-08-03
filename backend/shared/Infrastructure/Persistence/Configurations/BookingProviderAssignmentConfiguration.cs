using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class BookingProviderAssignmentConfiguration : IEntityTypeConfiguration<BookingProviderAssignment>
{
    public void Configure(EntityTypeBuilder<BookingProviderAssignment> builder)
    {
        builder.ToTable("booking_provider_assignment");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.BookingId).IsRequired();
        builder.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.AssignedByType).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.AssignedByUserId);
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.ResponseDeadline);
        builder.Property(x => x.RespondedAt);
        builder.Property(x => x.Notes).HasMaxLength(500);
        builder.Property(x => x.CompletionProofRef).HasMaxLength(500);

        // Several rows accumulate per booking over time (reassignment
        // history) - unlike BookingCancellation, no unique index on
        // BookingId. Lookups filter by (BookingId, Status) to find the one
        // currently "live" (Assigned/Accepted) row.
        builder.HasIndex(x => new { x.BookingId, x.Status });
        builder.HasIndex(x => x.ProviderId);
    }
}
