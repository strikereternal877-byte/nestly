using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class RecurringBookingPlanConfiguration : IEntityTypeConfiguration<RecurringBookingPlan>
{
    public void Configure(EntityTypeBuilder<RecurringBookingPlan> builder)
    {
        builder.ToTable("recurring_booking_plan");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CustomerId).IsRequired();
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // ServiceId/CityId/LocalityId/AddressId/SlotWindowId are live
        // references (see the class doc comment) - Restrict, same as
        // Booking.CustomerId, so none of these can be hard-deleted out from
        // under a plan still relying on them; a plan whose target becomes
        // inactive/unserviceable instead fails at the orchestration and is
        // skipped-and-notified like any other occurrence.
        builder.Property(x => x.ServiceId).IsRequired();
        builder.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.CityId).IsRequired();
        builder.HasOne<City>().WithMany().HasForeignKey(x => x.CityId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.LocalityId).IsRequired();
        builder.HasOne<Locality>().WithMany().HasForeignKey(x => x.LocalityId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.AddressId).IsRequired();
        builder.HasOne<CustomerAddress>().WithMany().HasForeignKey(x => x.AddressId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.SlotWindowId).IsRequired();
        builder.HasOne<SlotWindow>().WithMany().HasForeignKey(x => x.SlotWindowId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.Quantity).IsRequired();
        builder.Property(x => x.ApplyWalletCredit).IsRequired().HasDefaultValue(false);
        builder.Property(x => x.AutoChargeEnabled).IsRequired().HasDefaultValue(false);

        builder.Property(x => x.PauseReason).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.SkipUntilDate);
        builder.Property(x => x.SkipRangesUsed).IsRequired().HasDefaultValue(0);

        builder.Property(x => x.PrepaidUpfront).IsRequired().HasDefaultValue(false);
        builder.Property(x => x.PendingPrepaymentLeadBookingId);
        builder.Property(x => x.PrepaidCyclesPaid).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.PrepaidThroughDate);
        builder.Property(x => x.PendingPrepaymentThroughDate);

        builder.Property(x => x.Frequency).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.RecurrenceDayOfWeek).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.RecurrenceDayOfMonth);

        builder.Property(x => x.StartDate).IsRequired();
        builder.Property(x => x.EndDate);
        builder.Property(x => x.OccurrenceCount);
        builder.Property(x => x.CompletedOccurrenceCount).IsRequired();
        builder.Property(x => x.NextOccurrenceDate).IsRequired();

        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasMany(x => x.AddOns)
            .WithOne()
            .HasForeignKey(x => x.RecurringBookingPlanId)
            .OnDelete(DeleteBehavior.Cascade);

        // The scheduler's due-set query (IRecurringBookingPlanRepository.ListDueAsync)
        // filters on exactly these two columns together.
        builder.HasIndex(x => new { x.Status, x.NextOccurrenceDate });
        builder.HasIndex(x => x.CustomerId);
    }
}
