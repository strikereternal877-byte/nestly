using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class BookingItemConfiguration : IEntityTypeConfiguration<BookingItem>
{
    public void Configure(EntityTypeBuilder<BookingItem> builder)
    {
        builder.ToTable("booking_item");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.BookingId).IsRequired();

        // ServiceId is deliberately not a foreign key - see Booking's doc comment.
        builder.Property(x => x.ServiceId).IsRequired();
        builder.Property(x => x.NameSnapshot).IsRequired().HasMaxLength(200);
        builder.Property(x => x.SlugSnapshot).IsRequired().HasMaxLength(200);
        builder.Property(x => x.UnitPriceSnapshot).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.Quantity).IsRequired();
        builder.Property(x => x.LineTotalSnapshot).IsRequired().HasPrecision(12, 2);

        // Phase 3 catalog redesign: ServiceVariantId is deliberately not a
        // foreign key, same convention as ServiceId above. Null when the
        // item was booked without selecting a variant (today's default).
        builder.Property(x => x.ServiceVariantId);
        builder.Property(x => x.VariantNameSnapshot).HasMaxLength(200);
        builder.Property(x => x.VariantDurationMinutesSnapshot);

        // Appliance/Service Group catalog redesign: ServiceGroupId is
        // deliberately not a foreign key, same convention as ServiceId
        // above. Null when the service wasn't grouped at booking time
        // (today's default for every existing service).
        builder.Property(x => x.ServiceGroupId);
        builder.Property(x => x.ServiceGroupNameSnapshot).HasMaxLength(200);

        // A cook booking's meals, people and food preferences (docs/MEAL-SERVICE.md).
        builder.Property(x => x.MealsMask).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.PersonsSnapshot);
        MealPreferencesColumn.Configure(builder.Property(x => x.MealPreferencesSnapshot));

        builder.HasMany(x => x.AddOns)
            .WithOne()
            .HasForeignKey(x => x.BookingItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.BookingId);
    }
}
