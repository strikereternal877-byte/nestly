namespace Nestly.Domain.Meals;

/// <summary>
/// How one meal of a meal-priced <see cref="Service"/> is priced and timed
/// (docs/MEAL-SERVICE.md): <see cref="BaseRate"/> covers up to
/// <see cref="IncludedPersons"/>, each further person adds
/// <see cref="ExtraPersonRate"/>; cooking time grows the same way.
/// </summary>
public sealed record MealOption(
    MealType Meal,
    decimal BaseRate,
    int IncludedPersons,
    decimal ExtraPersonRate,
    int BaseMinutes,
    int MinutesPerExtraPerson,
    TimeOnly DefaultStartTime)
{
    public decimal RateFor(int persons) => BaseRate + ExtraPersons(persons) * ExtraPersonRate;

    public int MinutesFor(int persons) => BaseMinutes + ExtraPersons(persons) * MinutesPerExtraPerson;

    private int ExtraPersons(int persons) => Math.Max(0, persons - IncludedPersons);
}

/// <summary>
/// The price and length of one visit: the chosen meals that share a
/// <see cref="MealSession"/>, cooked for <see cref="Persons"/>.
/// <see cref="DefaultStartTime"/> is its first meal's usual time,
/// <see cref="LastMealStartTime"/> its last meal's.
/// </summary>
public sealed record MealVisitQuote(
    MealSession Session,
    IReadOnlyList<MealType> Meals,
    int Persons,
    decimal Rate,
    int DurationMinutes,
    TimeOnly DefaultStartTime,
    TimeOnly LastMealStartTime)
{
    /// <summary>A visit may start this much before its first meal's usual time, and at least this much after.</summary>
    public const int StartShiftMinutes = 60;

    private static readonly TimeOnly FirstStart = new(6, 0);
    private static readonly TimeOnly LastStart = new(21, 30);

    /// <summary>"Lunch + Dinner · 4 persons" - what the customer booked, in the words the cook needs.</summary>
    public string Describe() => $"{string.Join(" + ", Meals)} · {Persons} {(Persons == 1 ? "person" : "persons")}";

    /// <summary>
    /// Earliest start a monthly visit may be moved to (docs/MEAL-SERVICE.md):
    /// an hour before its first meal's usual time, not before 06:00 or its session.
    /// </summary>
    public TimeOnly EarliestStart => FromMinutes(Math.Max(
        Math.Max(Minutes(FirstStart), Session == MealSession.Evening ? Minutes(MealTypeExtensions.EveningStartsAt) : 0),
        Minutes(DefaultStartTime) - StartShiftMinutes));

    /// <summary>
    /// Latest start: its last meal's usual time, or an hour after its first
    /// meal's if that is later - so cooking several meals in one visit can
    /// still be timed for the last of them. Not after 21:30, and a morning
    /// visit stays before the evening.
    /// </summary>
    public TimeOnly LatestStart => FromMinutes(Math.Min(
        Math.Min(Minutes(LastStart), Session == MealSession.Morning ? Minutes(MealTypeExtensions.EveningStartsAt) - 30 : int.MaxValue),
        Math.Max(Minutes(LastMealStartTime), Minutes(DefaultStartTime) + StartShiftMinutes)));

    public bool AllowsStart(TimeOnly start) => start >= EarliestStart && start <= LatestStart;

    private static int Minutes(TimeOnly time) => time.Hour * 60 + time.Minute;

    private static TimeOnly FromMinutes(int minutes) => new(minutes / 60, minutes % 60);
}

/// <summary>Pure pricing rules for meal-priced services. Throws <see cref="ArgumentException"/> on bad input, like the entities do.</summary>
public static class MealPricing
{
    public const int MaxPersons = 12;
    public const decimal MaxRate = 100_000m;
    public const int MinMinutes = 15;
    public const int MaxMinutes = 480;
    public const int MaxMinutesPerExtraPerson = 60;

    public static void ValidateOptions(IReadOnlyCollection<MealOption> options)
    {
        if (options.Count == 0)
        {
            throw new ArgumentException("Offer at least one meal.", nameof(options));
        }

        if (options.Select(o => o.Meal).Distinct().Count() != options.Count)
        {
            throw new ArgumentException("Each meal can be listed only once.", nameof(options));
        }

        foreach (var option in options)
        {
            if (!Enum.IsDefined(option.Meal))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Unknown meal.");
            }

            if (option.BaseRate is <= 0 or > MaxRate)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"{option.Meal}: rate must be between 1 and {MaxRate}.");
            }

            if (option.ExtraPersonRate is < 0 or > MaxRate)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"{option.Meal}: extra person rate must be between 0 and {MaxRate}.");
            }

            if (option.IncludedPersons is < 1 or > MaxPersons)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"{option.Meal}: included persons must be between 1 and {MaxPersons}.");
            }

            if (option.BaseMinutes is < MinMinutes or > MaxMinutes)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"{option.Meal}: cooking time must be between {MinMinutes} and {MaxMinutes} minutes.");
            }

            if (option.MinutesPerExtraPerson is < 0 or > MaxMinutesPerExtraPerson)
            {
                throw new ArgumentOutOfRangeException(nameof(options), $"{option.Meal}: extra time per person must be between 0 and {MaxMinutesPerExtraPerson} minutes.");
            }

            if (!option.Meal.Session().Contains(option.DefaultStartTime))
            {
                throw new ArgumentOutOfRangeException(nameof(options),
                    $"{option.Meal}: default time must be {(option.Meal.Session() == MealSession.Morning ? "before" : "from")} {MealTypeExtensions.EveningStartsAt:HH:mm}.");
            }
        }
    }

    /// <summary>
    /// One quote per session the chosen meals fall in, morning first. Each
    /// session's visit starts at the earliest default time of its meals, and
    /// may be moved within <see cref="MealVisitQuote.EarliestStart"/> -
    /// <see cref="MealVisitQuote.LatestStart"/>.
    /// </summary>
    public static IReadOnlyList<MealVisitQuote> Quote(IReadOnlyCollection<MealOption> options, IEnumerable<MealType> meals, int persons)
    {
        if (persons is < 1 or > MaxPersons)
        {
            throw new ArgumentOutOfRangeException(nameof(persons), $"Cooking is for 1 to {MaxPersons} persons.");
        }

        var chosen = meals.Distinct().OrderBy(m => m).ToList();
        if (chosen.Count == 0)
        {
            throw new ArgumentException("Choose at least one meal.", nameof(meals));
        }

        var byMeal = options.ToDictionary(o => o.Meal);
        var notOffered = chosen.Where(m => !byMeal.ContainsKey(m)).ToList();
        if (notOffered.Count > 0)
        {
            throw new ArgumentException($"{string.Join(", ", notOffered)} not offered for this service.", nameof(meals));
        }

        return chosen
            .GroupBy(m => m.Session())
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var picked = g.Select(m => byMeal[m]).ToList();
                return new MealVisitQuote(
                    g.Key,
                    g.ToList(),
                    persons,
                    picked.Sum(o => o.RateFor(persons)),
                    picked.Sum(o => o.MinutesFor(persons)),
                    picked.Min(o => o.DefaultStartTime),
                    picked.Max(o => o.DefaultStartTime));
            })
            .ToList();
    }
}
