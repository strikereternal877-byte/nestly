using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderSkillMappingConfiguration : IEntityTypeConfiguration<ProviderSkillMapping>
{
    public void Configure(EntityTypeBuilder<ProviderSkillMapping> builder)
    {
        builder.ToTable("provider_skill_mapping");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.IsActive).IsRequired();

        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.CategoryId).IsRequired();
        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.ServiceId);
        builder.HasOne<Service>()
            .WithMany()
            .HasForeignKey(x => x.ServiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ProviderId, x.CategoryId, x.ServiceId }).IsUnique();
        builder.HasIndex(x => x.CategoryId);
    }
}
