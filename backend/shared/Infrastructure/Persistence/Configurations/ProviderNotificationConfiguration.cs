using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderNotificationConfiguration : IEntityTypeConfiguration<ProviderNotification>
{
    public void Configure(EntityTypeBuilder<ProviderNotification> builder)
    {
        builder.ToTable("provider_notification");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderId).IsRequired();
        builder.Property(x => x.Type).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.Title).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Body).IsRequired().HasMaxLength(1000);
        builder.Property(x => x.DeepLinkPath).HasMaxLength(200);
        builder.Property(x => x.IsRead).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        // Backs both ListByProviderAsync's ordering and CountUnreadByProviderAsync's filter.
        builder.HasIndex(x => new { x.ProviderId, x.CreatedAtUtc });
        builder.HasIndex(x => new { x.ProviderId, x.IsRead });
    }
}
