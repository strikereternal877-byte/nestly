using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderAvailabilityWindowConfiguration : IEntityTypeConfiguration<ProviderAvailabilityWindow>
{
    public void Configure(EntityTypeBuilder<ProviderAvailabilityWindow> builder)
    {
        builder.ToTable("provider_availability_window");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.DayOfWeek).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(x => x.StartTime).IsRequired();
        builder.Property(x => x.EndTime).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();

        builder.HasIndex(x => new { x.ProviderId, x.DayOfWeek });
    }
}
