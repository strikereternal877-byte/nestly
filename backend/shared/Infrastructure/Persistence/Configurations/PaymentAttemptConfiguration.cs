using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> builder)
    {
        builder.ToTable("payment_attempt");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PaymentTransactionId).IsRequired();
        builder.Property(x => x.AttemptNumber).IsRequired();
        builder.Property(x => x.GatewayOrderId).IsRequired().HasMaxLength(100);
        builder.HasIndex(x => x.GatewayOrderId).IsUnique();
        builder.Property(x => x.GatewayPaymentRef).HasMaxLength(100);
        builder.Property(x => x.PaymentGroupId);
        builder.HasOne<PaymentGroup>()
            .WithMany()
            .HasForeignKey(x => x.PaymentGroupId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.PaymentGroupId);
        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.FailureReason).HasMaxLength(500);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.CompletedAtUtc);

        builder.HasIndex(x => new { x.PaymentTransactionId, x.AttemptNumber }).IsUnique();
    }
}
