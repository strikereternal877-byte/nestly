using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderConfiguration : IEntityTypeConfiguration<Provider>
{
    public void Configure(EntityTypeBuilder<Provider> builder)
    {
        builder.ToTable("provider");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.LegalName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.ProviderType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Phone).IsRequired().HasMaxLength(20);
        builder.HasIndex(x => x.Phone).IsUnique();
        builder.Property(x => x.Email).HasMaxLength(200);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.OnboardingStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();
    }
}
