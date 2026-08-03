using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderOtpConfiguration : IEntityTypeConfiguration<ProviderOtp>
{
    public void Configure(EntityTypeBuilder<ProviderOtp> builder)
    {
        builder.ToTable("provider_otp");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProviderId);
        builder.HasIndex(x => x.ProviderId);
        builder.Property(x => x.Target).IsRequired().HasMaxLength(200);
        builder.HasIndex(x => x.Target);
        builder.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.CodeHash).IsRequired().HasMaxLength(500);
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.AttemptCount).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
    }
}
