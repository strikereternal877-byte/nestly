using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class SlotWindowConfiguration : IEntityTypeConfiguration<SlotWindow>
{
    public void Configure(EntityTypeBuilder<SlotWindow> builder)
    {
        builder.ToTable("slot_window");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.StartTime).IsRequired();
        builder.Property(x => x.EndTime).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.MaxBookingsPerSlot);

        builder.Property(x => x.CityId).IsRequired();
        builder.HasOne(x => x.City)
            .WithMany()
            .HasForeignKey(x => x.CityId)
            .OnDelete(DeleteBehavior.Restrict);
        // Composite, not a plain CityId index (backend response time fix):
        // the /slots hot path (SlotWindowRepository.ListActiveForCityAndDayAsync,
        // called on every SlotAvailabilityService.GetAvailableSlotsAsync
        // request) filters CityId + IsActive together, same reasoning as
        // Service's CategoryId+IsActive composite. The leading column still
        // serves ListAsync's cityId-only filter (admin slot-window listing),
        // so a separate single-column index would be redundant overhead.
        builder.HasIndex(x => new { x.CityId, x.IsActive });
    }
}
