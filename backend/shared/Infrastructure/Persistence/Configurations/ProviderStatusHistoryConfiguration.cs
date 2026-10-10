using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderStatusHistoryConfiguration : IEntityTypeConfiguration<ProviderStatusHistory>
{
    public void Configure(EntityTypeBuilder<ProviderStatusHistory> builder)
    {
        builder.ToTable("provider_status_history");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderId).IsRequired();
        builder.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.ToStatus).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.ChangedAtUtc).IsRequired();

        builder.HasIndex(x => new { x.ProviderId, x.ChangedAtUtc });
    }
}
