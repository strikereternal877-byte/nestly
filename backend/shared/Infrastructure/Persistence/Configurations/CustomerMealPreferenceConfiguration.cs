using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain.Meals;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class CustomerMealPreferenceConfiguration : IEntityTypeConfiguration<CustomerMealPreference>
{
    public void Configure(EntityTypeBuilder<CustomerMealPreference> builder)
    {
        builder.ToTable("customer_meal_preference");
        builder.HasKey(x => x.Id);

        // One saved set per customer; the unique index makes a concurrent
        // double-create fail loudly instead of keeping two.
        builder.Property(x => x.CustomerId).IsRequired();
        builder.HasIndex(x => x.CustomerId).IsUnique();

        MealPreferencesColumn.Configure(builder.Property(x => x.Preferences)!).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();
    }
}
