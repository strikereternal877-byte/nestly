using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderBackgroundCheckConfiguration : IEntityTypeConfiguration<ProviderBackgroundCheck>
{
    public void Configure(EntityTypeBuilder<ProviderBackgroundCheck> builder)
    {
        builder.ToTable("provider_background_check");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.CheckedBy).IsRequired();
        builder.Property(x => x.CheckedAt).IsRequired();
        builder.Property(x => x.Notes).HasMaxLength(1000);

        // Append-only (re-checks add rows); the activation gate always reads
        // the most recent row per provider.
        builder.HasIndex(x => new { x.ProviderId, x.CheckedAt });
    }
}
