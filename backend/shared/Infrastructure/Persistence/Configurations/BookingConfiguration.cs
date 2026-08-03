using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("booking");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.Property(x => x.CustomerId).IsRequired();
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.CustomerNameSnapshot).IsRequired().HasMaxLength(200);
        builder.Property(x => x.CustomerMobileSnapshot).IsRequired().HasMaxLength(20);

        // SourceAddressId is deliberately not a foreign key - see CustomerAddress's
        // doc comment. Deleting the source address must never touch this row.
        builder.Property(x => x.SourceAddressId);
        builder.Property(x => x.AddressLabelSnapshot).IsRequired().HasMaxLength(100);
        builder.Property(x => x.AddressLine1Snapshot).IsRequired().HasMaxLength(300);
        builder.Property(x => x.AddressLine2Snapshot).HasMaxLength(300);
        builder.Property(x => x.AddressLandmarkSnapshot).HasMaxLength(200);
        builder.Property(x => x.AddressPincodeSnapshot).IsRequired().HasMaxLength(10);
        builder.Property(x => x.AddressCitySnapshot).IsRequired().HasMaxLength(100);
        builder.Property(x => x.AddressStateSnapshot).IsRequired().HasMaxLength(100);
        builder.Property(x => x.AddressLatitudeSnapshot).HasPrecision(9, 6);
        builder.Property(x => x.AddressLongitudeSnapshot).HasPrecision(9, 6);
        builder.Property(x => x.AddressContactNameSnapshot).IsRequired().HasMaxLength(200);
        builder.Property(x => x.AddressContactMobileSnapshot).IsRequired().HasMaxLength(20);

        // SlotWindowId is deliberately not a foreign key, for the same reason -
        // a later admin edit/deactivation of the window must not touch history.
        builder.Property(x => x.SlotWindowId).IsRequired();
        builder.Property(x => x.SlotDate).IsRequired();
        builder.Property(x => x.SlotWindowNameSnapshot).IsRequired().HasMaxLength(100);
        builder.Property(x => x.SlotStartTimeSnapshot).IsRequired();
        builder.Property(x => x.SlotEndTimeSnapshot).IsRequired();

        builder.Property(x => x.BasePriceSnapshot).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.QuantitySnapshot).IsRequired();
        builder.Property(x => x.BaseTotalSnapshot).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.AddOnTotalSnapshot).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.VisitChargeSnapshot).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.SubtotalSnapshot).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.TaxPercentageSnapshot).IsRequired().HasPrecision(5, 2);
        builder.Property(x => x.TaxAmountSnapshot).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.PlatformFeeSnapshot).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.TotalPayableSnapshot).IsRequired().HasPrecision(12, 2);

        builder.Property(x => x.CouponCodeSnapshot).HasMaxLength(50);
        builder.Property(x => x.CouponDiscountAmountSnapshot).HasPrecision(12, 2);

        // Task 179: traceability only, not a foreign key - see SubscriptionId's doc comment.
        builder.Property(x => x.SubscriptionId);
        builder.Property(x => x.SubscriptionFreeVisitApplied).IsRequired();
        builder.Property(x => x.SubscriptionDiscountAmountSnapshot).HasPrecision(12, 2);

        // Denormalized display field only (task 147, PROVIDER.md SCOPE
        // BOUNDARY) - SetNull rather than Restrict/Cascade so a provider
        // record can still be soft-managed without ever blocking on or
        // corrupting historical bookings; the authoritative record is
        // BookingProviderAssignment, not this column.
        builder.Property(x => x.AssignedProviderId);
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.AssignedProviderId)
            .OnDelete(DeleteBehavior.SetNull);
        // No explicit HasIndex needed here (checked for task 136b,
        // BookingRepository.ListByAssignedProviderAsync's filter column): EF
        // Core creates ix_booking_assigned_provider_id automatically for this
        // foreign key by convention - confirmed in
        // database/migrations/NestlyDbContextModelSnapshot.cs. An explicit
        // duplicate would violate DATABASE.md's "avoid excessive indexing."

        builder.HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.StatusHistory)
            .WithOne()
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.CustomerId, x.Status });
        builder.HasIndex(x => x.CreatedAtUtc);
    }
}
