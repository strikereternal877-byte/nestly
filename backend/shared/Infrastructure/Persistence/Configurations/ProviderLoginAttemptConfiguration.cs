using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderLoginAttemptConfiguration : IEntityTypeConfiguration<ProviderLoginAttempt>
{
    public void Configure(EntityTypeBuilder<ProviderLoginAttempt> builder)
    {
        builder.ToTable("provider_login_attempt");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Identifier).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Succeeded).IsRequired();
        builder.Property(x => x.OccurredAtUtc).IsRequired();
        builder.HasIndex(x => new { x.Identifier, x.OccurredAtUtc });
    }
}
