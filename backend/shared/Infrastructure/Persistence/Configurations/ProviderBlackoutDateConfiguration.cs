using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderBlackoutDateConfiguration : IEntityTypeConfiguration<ProviderBlackoutDate>
{
    public void Configure(EntityTypeBuilder<ProviderBlackoutDate> builder)
    {
        builder.ToTable("provider_blackout_date");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.StartDate).IsRequired();
        builder.Property(x => x.EndDate).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500);

        builder.HasIndex(x => new { x.ProviderId, x.StartDate, x.EndDate });
    }
}
