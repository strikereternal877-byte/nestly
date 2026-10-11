using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain.Meals;

namespace Nestly.Infrastructure.Persistence.Configurations;

/// <summary>
/// <see cref="MealPreferences"/> as one JSON text column - small, always read
/// and written whole, and the same shape on the customer, the booking item and
/// the monthly contract. Text rather than jsonb for SQLite test portability,
/// like <see cref="MealOptionsColumn"/>. Enum names, so the column reads
/// plainly in SQL.
/// </summary>
internal static class MealPreferencesColumn
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static PropertyBuilder<MealPreferences?> Configure(PropertyBuilder<MealPreferences?> property) =>
        property
            .HasColumnType("text")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Json),
                c => string.IsNullOrEmpty(c) ? null : JsonSerializer.Deserialize<MealPreferences>(c, Json),
                new ValueComparer<MealPreferences?>((a, b) => Equals(a, b), v => v == null ? 0 : v.GetHashCode(), v => v));
}
