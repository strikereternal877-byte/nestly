using FluentValidation;
using Nestly.Application.MonthlyService;
using Nestly.Domain.Meals;

namespace Nestly.Application.Meals;

/// <summary>One meal's rate and timing as it crosses the wire (docs/MEAL-SERVICE.md). Times are "HH:mm".</summary>
public sealed record MealOptionDto(
    MealType Meal,
    decimal BaseRate,
    int IncludedPersons,
    decimal ExtraPersonRate,
    int BaseMinutes,
    int MinutesPerExtraPerson,
    string DefaultStartTime);

/// <summary>What the customer chose for a meal-priced service: which meals, for how many people.</summary>
public sealed record MealSelection(IReadOnlyList<MealType> Meals, int Persons);

/// <summary>Replaces every meal rate of a meal-priced service at once.</summary>
public sealed record MealOptionsUpdateRequest(IReadOnlyList<MealOptionDto> Options);

public static class MealOptionMapping
{
    public static MealOptionDto ToDto(this MealOption option) => new(
        option.Meal, option.BaseRate, option.IncludedPersons, option.ExtraPersonRate,
        option.BaseMinutes, option.MinutesPerExtraPerson, MonthlyServiceTimeFormat.Format(option.DefaultStartTime));

    public static IReadOnlyList<MealOptionDto> ToDtos(this IEnumerable<MealOption> options) => options.Select(ToDto).ToList();

    /// <summary>Times are already checked by <see cref="MealOptionsUpdateRequestValidator"/>.</summary>
    public static MealOption ToDomain(this MealOptionDto dto)
    {
        MonthlyServiceTimeFormat.TryParse(dto.DefaultStartTime, out var start);
        return new MealOption(dto.Meal, dto.BaseRate, dto.IncludedPersons, dto.ExtraPersonRate, dto.BaseMinutes, dto.MinutesPerExtraPerson, start);
    }

    /// <summary>A cache/idempotency-safe key for a selection: same meals in any order give the same key.</summary>
    public static string Key(this MealSelection selection) =>
        $"{string.Join('-', selection.Meals.Distinct().OrderBy(m => m).Select(m => (int)m))}x{selection.Persons}";
}

public class MealOptionsUpdateRequestValidator : AbstractValidator<MealOptionsUpdateRequest>
{
    public MealOptionsUpdateRequestValidator()
    {
        RuleFor(x => x.Options).NotEmpty().WithMessage("Offer at least one meal.");
        RuleFor(x => x.Options)
            .Must(o => o is null || o.Select(m => m.Meal).Distinct().Count() == o.Count)
            .WithMessage("Each meal can be listed only once.");
        RuleForEach(x => x.Options).ChildRules(option =>
        {
            option.RuleFor(o => o.Meal).IsInEnum();
            option.RuleFor(o => o.BaseRate).GreaterThan(0).LessThanOrEqualTo(MealPricing.MaxRate);
            option.RuleFor(o => o.ExtraPersonRate).GreaterThanOrEqualTo(0).LessThanOrEqualTo(MealPricing.MaxRate);
            option.RuleFor(o => o.IncludedPersons).InclusiveBetween(1, MealPricing.MaxPersons);
            option.RuleFor(o => o.BaseMinutes).InclusiveBetween(MealPricing.MinMinutes, MealPricing.MaxMinutes);
            option.RuleFor(o => o.MinutesPerExtraPerson).InclusiveBetween(0, MealPricing.MaxMinutesPerExtraPerson);
            option.RuleFor(o => o.DefaultStartTime)
                .Must(v => MonthlyServiceTimeFormat.TryParse(v, out _))
                .WithMessage("Default time must be in HH:mm format.");
        });
    }
}

/// <summary>Shape checks only; whether the meals are offered and the count is in range is the pricing rule's call.</summary>
public class MealSelectionValidator : AbstractValidator<MealSelection>
{
    public MealSelectionValidator()
    {
        RuleFor(x => x.Meals).NotEmpty().WithMessage("Choose at least one meal.");
        RuleForEach(x => x.Meals).IsInEnum();
        RuleFor(x => x.Persons).InclusiveBetween(1, MealPricing.MaxPersons);
    }
}
