using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain.Meals;

/// <summary>What the household eats (docs/MEAL-SERVICE.md FOOD PREFERENCES).</summary>
public enum DietType
{
    Veg,
    NonVeg,
    Eggetarian,
    Jain,
    NoOnionGarlic,
}

public enum SpiceLevel
{
    Mild,
    Medium,
    Spicy,
}

public enum Cuisine
{
    NorthIndian,
    Rajasthani,
    SouthIndian,
    Gujarati,
    Punjabi,
    Bengali,
}

/// <summary>
/// How a cook should cook for this household: diet, spice and the cuisines
/// they like (none means any). Asked when a cook is booked, kept on the
/// customer for next time, and copied onto each booking and monthly contract
/// so the cook sees what was asked when it was booked.
/// </summary>
public sealed record MealPreferences
{
    public DietType Diet { get; init; }
    public SpiceLevel Spice { get; init; }
    public IReadOnlyList<Cuisine> Cuisines { get; init; } = [];

    /// <summary>Validated, with the cuisines de-duplicated and in a stable order.</summary>
    public static MealPreferences Create(DietType diet, SpiceLevel spice, IEnumerable<Cuisine> cuisines)
    {
        if (!Enum.IsDefined(diet))
        {
            throw new ArgumentOutOfRangeException(nameof(diet), "Unknown diet.");
        }

        if (!Enum.IsDefined(spice))
        {
            throw new ArgumentOutOfRangeException(nameof(spice), "Unknown spice level.");
        }

        var chosen = cuisines.Distinct().OrderBy(c => c).ToList();
        if (chosen.Any(c => !Enum.IsDefined(c)))
        {
            throw new ArgumentOutOfRangeException(nameof(cuisines), "Unknown cuisine.");
        }

        return new MealPreferences { Diet = diet, Spice = spice, Cuisines = chosen };
    }

    public bool Equals(MealPreferences? other) =>
        other is not null && Diet == other.Diet && Spice == other.Spice && Cuisines.SequenceEqual(other.Cuisines);

    public override int GetHashCode() => HashCode.Combine(Diet, Spice, string.Join(',', Cuisines));
}

/// <summary>A customer's saved <see cref="MealPreferences"/>, pre-filled the next time they book a cook.</summary>
public class CustomerMealPreference : Entity<Guid>
{
    public Guid CustomerId { get; private set; }
    public MealPreferences Preferences { get; private set; } = null!;
    public DateTime UpdatedAtUtc { get; private set; }

    protected CustomerMealPreference() { }

    public CustomerMealPreference(Guid id, Guid customerId, MealPreferences preferences, DateTime nowUtc) : base(id)
    {
        CustomerId = customerId;
        Preferences = preferences;
        UpdatedAtUtc = nowUtc;
    }

    public void Update(MealPreferences preferences, DateTime nowUtc)
    {
        Preferences = preferences;
        UpdatedAtUtc = nowUtc;
    }
}
