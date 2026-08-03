using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderSessionConfiguration : IEntityTypeConfiguration<ProviderSession>
{
    public void Configure(EntityTypeBuilder<ProviderSession> builder)
    {
        builder.ToTable("provider_session");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasIndex(x => x.ProviderId);
        builder.Property(x => x.RefreshTokenHash).IsRequired().HasMaxLength(500);
        builder.HasIndex(x => x.RefreshTokenHash).IsUnique();
        builder.Property(x => x.IssuedAt).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.DeviceInfo).HasMaxLength(500);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
    }
}
