using FluentAssertions;
using Nestly.Application.Meals;
using Nestly.Application.Pricing;
using Nestly.Domain;
using Nestly.Domain.Meals;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// docs/MEAL-SERVICE.md: a cook is priced per meal and persons. The pure
/// rules first, then the one-time booking price against a real (SQLite)
/// database - which also proves the meal rates survive the JSON column.
/// </summary>
public sealed class MealPricingTests : IClassFixture<TestDatabase>
{
    /// <summary>Breakfast Rs 100 for 2 (+30/person), lunch and dinner Rs 150 for 2 (+40/person).</summary>
    public static readonly MealOption[] CookMeals =
    [
        new(MealType.Breakfast, 100m, 2, 30m, 45, 10, new TimeOnly(7, 30)),
        new(MealType.Lunch, 150m, 2, 40m, 60, 10, new TimeOnly(12, 0)),
        new(MealType.Dinner, 150m, 2, 40m, 60, 10, new TimeOnly(19, 0)),
    ];

    private readonly TestDatabase _db;

    public MealPricingTests(TestDatabase db) => _db = db;

    // ---- Rules ----

    [Fact]
    public void Extra_people_raise_the_rate_and_time_only_beyond_the_included_count()
    {
        var two = MealPricing.Quote(CookMeals, [MealType.Lunch], 2).Single();
        two.Rate.Should().Be(150m);
        two.DurationMinutes.Should().Be(60);

        var five = MealPricing.Quote(CookMeals, [MealType.Lunch], 5).Single();
        five.Rate.Should().Be(150m + 3 * 40m);
        five.DurationMinutes.Should().Be(60 + 3 * 10);
        five.Describe().Should().Be("Lunch · 5 persons");
    }

    [Fact]
    public void Morning_and_evening_meals_are_two_visits_each_priced_and_timed_on_its_own()
    {
        var quotes = MealPricing.Quote(CookMeals, [MealType.Dinner, MealType.Lunch, MealType.Breakfast], 3);

        quotes.Select(q => q.Session).Should().Equal(MealSession.Morning, MealSession.Evening);

        var morning = quotes[0];
        morning.Meals.Should().Equal(MealType.Breakfast, MealType.Lunch);
        morning.Rate.Should().Be(130m + 190m);
        morning.DurationMinutes.Should().Be(55 + 70);
        morning.DefaultStartTime.Should().Be(new TimeOnly(7, 30), "the visit starts with its earliest meal");

        var evening = quotes[1];
        evening.Meals.Should().Equal(MealType.Dinner);
        evening.Rate.Should().Be(190m);
        evening.DefaultStartTime.Should().Be(new TimeOnly(19, 0));
    }

    [Fact]
    public void A_visit_may_start_from_an_hour_before_its_first_meal_to_its_last_meal()
    {
        var morning = MealPricing.Quote(CookMeals, [MealType.Breakfast, MealType.Lunch], 2).Single();
        morning.LastMealStartTime.Should().Be(new TimeOnly(12, 0));
        morning.EarliestStart.Should().Be(new TimeOnly(6, 30));
        morning.LatestStart.Should().Be(new TimeOnly(12, 0), "timed for lunch, the visit's last meal");
        morning.AllowsStart(new TimeOnly(11, 0)).Should().BeTrue();
        morning.AllowsStart(new TimeOnly(12, 30)).Should().BeFalse();
        morning.AllowsStart(new TimeOnly(6, 0)).Should().BeFalse();

        var dinner = MealPricing.Quote(CookMeals, [MealType.Dinner], 2).Single();
        dinner.EarliestStart.Should().Be(new TimeOnly(18, 0));
        dinner.LatestStart.Should().Be(new TimeOnly(20, 0), "one meal: an hour either side of its usual time");

        var lateLunch = CookMeals[1] with { DefaultStartTime = new TimeOnly(14, 30) };
        MealPricing.Quote([lateLunch], [MealType.Lunch], 2).Single().LatestStart
            .Should().Be(new TimeOnly(14, 30), "a morning visit never starts in the evening");
    }

    [Fact]
    public void Quote_rejects_nothing_chosen_unoffered_meals_and_out_of_range_people()
    {
        FluentActions.Invoking(() => MealPricing.Quote(CookMeals, [], 2)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => MealPricing.Quote(CookMeals, [MealType.Snacks], 2))
            .Should().Throw<ArgumentException>().WithMessage("*Snacks*");
        FluentActions.Invoking(() => MealPricing.Quote(CookMeals, [MealType.Lunch], 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => MealPricing.Quote(CookMeals, [MealType.Lunch], MealPricing.MaxPersons + 1))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Meal_rates_belong_to_meal_priced_services_and_set_the_listing_price()
    {
        var service = new Service(Guid.NewGuid(), Guid.NewGuid(), "Home Cook", "home-cook", "desc", 999m);
        FluentActions.Invoking(() => service.SetMealOptions(CookMeals)).Should().Throw<InvalidOperationException>();

        service.SetPricingType(ServicePricingType.MealBased);
        service.SetMealOptions(CookMeals);
        service.Price.Should().Be(100m, "the listing card shows the cheapest meal");

        var dinnerAtNoon = CookMeals[2] with { DefaultStartTime = new TimeOnly(12, 0) };
        FluentActions.Invoking(() => service.SetMealOptions([dinnerAtNoon])).Should().Throw<ArgumentOutOfRangeException>();

        FluentActions.Invoking(() => service.SetMealOptions([CookMeals[1], CookMeals[1]])).Should().Throw<ArgumentException>();
    }

    // ---- One-time booking price ----

    [Fact]
    public async Task One_time_price_comes_from_the_meals_and_names_them_for_the_booking()
    {
        var (serviceId, cityId) = await SeedCookAsync();

        // Read back through a fresh context: the rates must survive the column.
        await using (var fresh = _db.CreateContext())
        {
            var reloaded = await new ServiceRepository(fresh).GetByIdAsync(serviceId);
            reloaded!.MealOptions.Should().Equal(CookMeals);
        }

        await using var context = _db.CreateContext();
        var pricing = NewPricing(context);

        var lunchForFour = await pricing.CalculateAsync(
            new PriceCalculationRequest(serviceId, cityId, 7, [], Meals: new MealSelection([MealType.Lunch], 4)));
        lunchForFour.IsSuccess.Should().BeTrue(lunchForFour.IsFailure ? lunchForFour.Error.Message : null);
        lunchForFour.Value.BasePrice.Should().Be(230m);
        lunchForFour.Value.Quantity.Should().Be(1, "a cook is never multiplied by quantity");
        lunchForFour.Value.TotalPayable.Should().Be(230m);
        lunchForFour.Value.SelectedVariantName.Should().Be("Lunch · 4 persons");
        lunchForFour.Value.SelectedVariantDurationMinutes.Should().Be(80);

        var lunchForTwo = await pricing.CalculateAsync(
            new PriceCalculationRequest(serviceId, cityId, 1, [], Meals: new MealSelection([MealType.Lunch], 2)));
        lunchForTwo.Value.BasePrice.Should().Be(150m, "a different selection is never served another's cached price");

        var breakfastAndDinner = await pricing.CalculateAsync(
            new PriceCalculationRequest(serviceId, cityId, 1, [], Meals: new MealSelection([MealType.Breakfast, MealType.Dinner], 2)));
        breakfastAndDinner.Error.Code.Should().Be("Pricing.MealsSpanSessions");

        var noMeals = await pricing.CalculateAsync(new PriceCalculationRequest(serviceId, cityId, 1, []));
        noMeals.Error.Code.Should().Be("Pricing.MealsRequired");
    }

    [Fact]
    public async Task Meals_are_refused_for_a_service_that_is_not_meal_priced()
    {
        await using var context = _db.CreateContext();
        var (_, cityId) = await SeedCookAsync();
        var category = new Category(Guid.NewGuid(), "Plumbing", "plumbing-" + Guid.NewGuid().ToString("N")[..8], "desc");
        var fixedService = new Service(Guid.NewGuid(), category.Id, "Tap repair", "tap-" + Guid.NewGuid().ToString("N")[..8], "desc", 299m);
        context.AddRange(category, fixedService);
        await context.SaveChangesAsync();

        var result = await NewPricing(context).CalculateAsync(
            new PriceCalculationRequest(fixedService.Id, cityId, 1, [], Meals: new MealSelection([MealType.Lunch], 2)));

        result.Error.Code.Should().Be("Pricing.MealsNotApplicable");
    }

    private async Task<(Guid ServiceId, Guid CityId)> SeedCookAsync()
    {
        await using var context = _db.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var state = new State(Guid.NewGuid(), "Rajasthan", "RJ" + suffix[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Jaipur " + suffix);
        var category = new Category(Guid.NewGuid(), "Cooking", "cooking-" + suffix, "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Home Cook", "home-cook-" + suffix, "desc", 100m);
        service.SetPricingType(ServicePricingType.MealBased);
        service.SetMealOptions(CookMeals);
        context.AddRange(state, city, category, service);
        await context.SaveChangesAsync();
        return (service.Id, city.Id);
    }

    private static PriceCalculationService NewPricing(NestlyDbContext context) => new(
        new ServiceRepository(context),
        new ServiceAddOnRepository(context),
        new ServiceabilityRepository(context),
        new ServiceCityPriceRepository(context),
        new CityPricingPolicyRepository(context),
        new ServiceVariantRepository(context),
        new ServiceAddOnGroupRepository(context),
        new InMemoryCacheService());
}
