using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class PaymentGroupConfiguration : IEntityTypeConfiguration<PaymentGroup>
{
    public void Configure(EntityTypeBuilder<PaymentGroup> builder)
    {
        builder.ToTable("payment_group");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CustomerId).IsRequired();
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.LeadBookingId).IsRequired();
        builder.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(x => x.LeadBookingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.GatewayOrderId).IsRequired().HasMaxLength(100);
        builder.HasIndex(x => x.GatewayOrderId).IsUnique();

        builder.Property(x => x.TotalAmount).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(3);
        builder.Property(x => x.VisitCount).IsRequired();
        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.GatewayPaymentRef).HasMaxLength(100);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.CompletedAtUtc);

        // "The latest group for this lead booking" is the payment page's and the
        // verify endpoint's lookup.
        builder.HasIndex(x => new { x.LeadBookingId, x.CreatedAtUtc });
    }
}
