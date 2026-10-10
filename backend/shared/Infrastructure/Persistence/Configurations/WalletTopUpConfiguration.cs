using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class WalletTopUpConfiguration : IEntityTypeConfiguration<WalletTopUp>
{
    public void Configure(EntityTypeBuilder<WalletTopUp> builder)
    {
        builder.ToTable("wallet_top_up");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CustomerId).IsRequired();
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.Amount).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(3);

        builder.Property(x => x.GatewayOrderId).IsRequired().HasMaxLength(100);
        builder.HasIndex(x => x.GatewayOrderId).IsUnique();

        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.GatewayPaymentRef).HasMaxLength(100);
        builder.Property(x => x.FailureReason).HasMaxLength(500);

        // Plain id, no FK: the ledger entry is created in the same transaction that resolves the top-up, and the
        // ledger (append-only, referenced by source type + id) is the record of truth; this is a pointer back.
        builder.Property(x => x.WalletLedgerEntryId);

        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.CompletedAtUtc);

        builder.Property(x => x.ReviewReason).HasMaxLength(WalletTopUp.MaxReviewReasonLength);
        builder.Property(x => x.ReviewFlaggedAtUtc);
        builder.Ignore(x => x.NeedsReview);

        // The velocity limit and the "reuse a recent pending" lookup both read one customer's recent rows.
        builder.HasIndex(x => new { x.CustomerId, x.CreatedAtUtc });
        // The reconciliation sweep's work list: pending rows by age.
        builder.HasIndex(x => new { x.Status, x.CreatedAtUtc });
    }
}
