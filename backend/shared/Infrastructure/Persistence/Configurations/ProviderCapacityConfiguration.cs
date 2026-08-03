using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderCapacityConfiguration : IEntityTypeConfiguration<ProviderCapacity>
{
    public void Configure(EntityTypeBuilder<ProviderCapacity> builder)
    {
        builder.ToTable("provider_capacity");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ProviderId).IsUnique();

        builder.Property(x => x.MaxJobsPerDay);
        builder.Property(x => x.MaxJobsPerSlot);
    }
}
