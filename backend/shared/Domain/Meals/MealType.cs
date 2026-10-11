namespace Nestly.Domain.Meals;

/// <summary>A meal a cook can be booked for (docs/MEAL-SERVICE.md). Crosses the wire as its ordinal.</summary>
public enum MealType
{
    Breakfast,
    Lunch,
    Snacks,
    Dinner
}

/// <summary>
/// Which visit of the day a meal is cooked on. Breakfast and lunch are cooked
/// on the morning visit, snacks and dinner on the evening one, so a household
/// that wants both gets two visits a day (two monthly contracts, or two
/// one-time bookings) - each priced and timed on its own.
/// </summary>
public enum MealSession
{
    Morning,
    Evening
}

public static class MealTypeExtensions
{
    /// <summary>Visits starting before this are morning visits; from it onwards, evening ones.</summary>
    public static readonly TimeOnly EveningStartsAt = new(15, 0);

    public static MealSession Session(this MealType meal) =>
        meal is MealType.Breakfast or MealType.Lunch ? MealSession.Morning : MealSession.Evening;

    /// <summary>Whether a visit starting at <paramref name="start"/> belongs to <paramref name="session"/>.</summary>
    public static bool Contains(this MealSession session, TimeOnly start) =>
        session == MealSession.Morning ? start < EveningStartsAt : start >= EveningStartsAt;

    /// <summary>Meals as a bitmask (bit = ordinal), for compact storage on a contract.</summary>
    public static int ToMask(IEnumerable<MealType> meals) => meals.Aggregate(0, (mask, meal) => mask | (1 << (int)meal));

    public static IReadOnlyList<MealType> FromMask(int mask) =>
        Enum.GetValues<MealType>().Where(meal => (mask & (1 << (int)meal)) != 0).ToList();
}
