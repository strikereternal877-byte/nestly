using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderPayoutConfiguration : IEntityTypeConfiguration<ProviderPayout>
{
    public void Configure(EntityTypeBuilder<ProviderPayout> builder)
    {
        builder.ToTable("provider_payout");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.PeriodStart).IsRequired();
        builder.Property(x => x.PeriodEnd).IsRequired();
        builder.Property(x => x.TotalAmount).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.PayoutReference).HasMaxLength(100);
        builder.Property(x => x.Notes).HasMaxLength(500);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.HasIndex(x => new { x.ProviderId, x.PeriodStart, x.PeriodEnd });
        builder.HasIndex(x => x.Status);
    }
}
