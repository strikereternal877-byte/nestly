using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;
using Nestly.Domain.Meals;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> builder)
    {
        builder.ToTable("service");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CategoryId).IsRequired();
        // Composite, not a plain CategoryId index (task 136b): every hot-path
        // read (ServiceRepository.ListActiveByCategoryAsync/SearchActiveAsync)
        // filters on CategoryId + IsActive together, and this composite still
        // serves the few admin queries that filter on CategoryId alone
        // (ListAllAsync) since it's the leading column - a separate
        // single-column index would be redundant overhead (DATABASE.md:
        // "Avoid excessive indexing").
        builder.HasIndex(x => new { x.CategoryId, x.IsActive });
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Slug).IsRequired().HasMaxLength(200);
        builder.HasIndex(x => x.Slug).IsUnique();
        builder.Property(x => x.Description).IsRequired().HasMaxLength(2000);
        builder.Property(x => x.ShortDescription).HasMaxLength(500);
        builder.Property(x => x.CoverImageUrl).HasMaxLength(500);
        builder.Property(x => x.Price).HasColumnType("decimal(18,2)").IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.Inclusions).IsRequired().HasMaxLength(4000).HasDefaultValue(string.Empty);
        builder.Property(x => x.Exclusions).IsRequired().HasMaxLength(4000).HasDefaultValue(string.Empty);
        builder.Property(x => x.CancellationPolicy).HasMaxLength(2000);
        builder.Property(x => x.ReschedulePolicy).HasMaxLength(2000);
        builder.Property(x => x.DurationMinutes).IsRequired().HasDefaultValue(60);
        builder.Property(x => x.IsFeatured).IsRequired().HasDefaultValue(false);
        builder.Property(x => x.SortOrder).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.SeoTitle).HasMaxLength(200);
        builder.Property(x => x.SeoMetaDescription).HasMaxLength(500);
        builder.Property(x => x.PricingType).IsRequired().HasMaxLength(20).HasConversion<string>().HasDefaultValue(ServicePricingType.Fixed);
        builder.Property(x => x.IsTaxApplicable).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.IsAddOnAllowed).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.IsQuantityAllowed).IsRequired().HasDefaultValue(false);
        builder.Property(x => x.IsInspectionBased).IsRequired().HasDefaultValue(false);
        builder.Property(x => x.IsSlotRequired).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.IsAddressRequired).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.IsCustomerNoteAllowed).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.IsDurationBased).IsRequired().HasDefaultValue(false);
        builder.Property(x => x.MealOptions)
            .HasConversion(v => MealOptionsColumn.ToColumn(v), v => MealOptionsColumn.FromColumn(v), MealOptionsColumn.Comparer)
            .HasColumnName("meal_options")
            .UsePropertyAccessMode(PropertyAccessMode.Property)
            .IsRequired()
            .HasMaxLength(2000)
            .HasDefaultValue(new List<MealOption>());

        // SetNull (not Restrict/Cascade), mirroring ServiceAddOn.GroupId:
        // deleting a group ungroups its services rather than orphaning or
        // cascading - ServiceGroupManagementService additionally rejects
        // deleting a group that still has services, so this SetNull is a
        // safety net, not the primary guard.
        builder.Property(x => x.ServiceGroupId);
        builder.HasIndex(x => x.ServiceGroupId);
        builder.HasOne<ServiceGroup>()
            .WithMany()
            .HasForeignKey(x => x.ServiceGroupId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>
/// At most four short entries, only ever read and written whole with the
/// service, so stored as JSON text on the service row rather than a child
/// table - same reasoning (and SQLite test portability) as TaskListColumn.
/// </summary>
internal static class MealOptionsColumn
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly ValueComparer<IReadOnlyList<MealOption>> Comparer = new(
        (a, b) => (a ?? Array.Empty<MealOption>()).SequenceEqual(b ?? Array.Empty<MealOption>()),
        v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
        v => v.ToList());

    public static string ToColumn(IReadOnlyList<MealOption> options) => JsonSerializer.Serialize(options, Json);

    public static IReadOnlyList<MealOption> FromColumn(string column) =>
        string.IsNullOrEmpty(column) ? new List<MealOption>() : JsonSerializer.Deserialize<List<MealOption>>(column, Json) ?? [];
}
