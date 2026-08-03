using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderServiceAreaConfiguration : IEntityTypeConfiguration<ProviderServiceArea>
{
    public void Configure(EntityTypeBuilder<ProviderServiceArea> builder)
    {
        builder.ToTable("provider_service_area");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.IsActive).IsRequired();

        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.CityId).IsRequired();
        builder.HasOne<City>()
            .WithMany()
            .HasForeignKey(x => x.CityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.ZoneId);
        builder.HasOne<Zone>()
            .WithMany()
            .HasForeignKey(x => x.ZoneId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.PincodeId);
        builder.HasOne<Pincode>()
            .WithMany()
            .HasForeignKey(x => x.PincodeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ProviderId, x.CityId, x.ZoneId, x.PincodeId }).IsUnique();
        builder.HasIndex(x => x.CityId);
    }
}
