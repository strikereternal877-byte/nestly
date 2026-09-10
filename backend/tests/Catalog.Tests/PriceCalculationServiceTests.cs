using FluentAssertions;
using Nestly.Application.Pricing;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 47a-c (base/add-on/quantity) and 48 (full breakdown API logic).</summary>
public sealed class PriceCalculationServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public PriceCalculationServiceTests(TestDatabase db) => _db = db;

    // A fresh InMemoryCacheService per call (task: catalog/pricing caching fix)
    // - callers that build two services in the same test (rare, but see below)
    // get independent caches, exactly like two different requests would in
    // production with a shared Redis but different cache keys.
    private PriceCalculationService BuildService(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new ServiceRepository(context),
        new ServiceAddOnRepository(context),
        new ServiceabilityRepository(context),
        new ServiceCityPriceRepository(context),
        new CityPricingPolicyRepository(context),
        new ServiceVariantRepository(context),
        new ServiceAddOnGroupRepository(context),
        new InMemoryCacheService());

    private (Category category, Service service, State state, City city) SeedServiceAndCity(Nestly.Infrastructure.Persistence.NestlyDbContext context, decimal basePrice = 500m)
    {
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", basePrice);
        // A unit-measured service so the base-price-times-quantity path is
        // exercised: the pricing engine now forces quantity to 1 for services
        // where IsQuantityAllowed is false (the constructor default), so a
        // quantity multiplication test needs a service that actually allows it.
        service.SetOptions(
            isTaxApplicable: true,
            isAddOnAllowed: true,
            isQuantityAllowed: true,
            isInspectionBased: false,
            isSlotRequired: true,
            isAddressRequired: true,
            isCustomerNoteAllowed: true);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");

        context.Add(category);
        context.Add(service);
        context.States.Add(state);
        context.Cities.Add(city);
        context.SaveChanges();

        return (category, service, state, city);
    }

    [Fact]
    public async Task Base_price_times_quantity_with_no_addons_or_policy()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(service.Id, city.Id, 2, []));

        result.IsSuccess.Should().BeTrue();
        result.Value.BasePrice.Should().Be(500m);
        result.Value.BaseTotal.Should().Be(1000m);
        result.Value.TotalPayable.Should().Be(1000m);
    }

    [Fact]
    public async Task Quantity_is_forced_to_one_for_a_service_that_is_not_unit_measured()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        // A flat-rate service: quantity must never multiply its price, even
        // when a (tampered or buggy) client sends one.
        service.SetOptions(
            isTaxApplicable: true,
            isAddOnAllowed: true,
            isQuantityAllowed: false,
            isInspectionBased: false,
            isSlotRequired: true,
            isAddressRequired: true,
            isCustomerNoteAllowed: true);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(service.Id, city.Id, 5, []));

        result.IsSuccess.Should().BeTrue();
        result.Value.Quantity.Should().Be(1);
        result.Value.BaseTotal.Should().Be(500m);
        result.Value.TotalPayable.Should().Be(500m);
    }

    [Fact]
    public async Task Quantity_is_capped_at_the_maximum_for_a_unit_measured_service()
    {
        using var context = _db.CreateContext();
        // SeedServiceAndCity enables quantity; 10 x 99 (the cap) = 990, proving
        // a runaway 1000 is clamped rather than multiplied straight through.
        var (_, service, _, city) = SeedServiceAndCity(context, 10m);

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(service.Id, city.Id, 1000, []));

        result.IsSuccess.Should().BeTrue();
        result.Value.Quantity.Should().Be(99);
        result.Value.BaseTotal.Should().Be(990m);
    }

    [Fact]
    public async Task Addon_line_items_are_included_in_the_total()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var addOn = new ServiceAddOn(Guid.NewGuid(), service.Id, "Sofa Cleaning", 150m);
        context.Add(addOn);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(
            new PriceCalculationRequest(service.Id, city.Id, 1, [new AddOnSelection(addOn.Id, 2)]));

        result.IsSuccess.Should().BeTrue();
        result.Value.AddOnLineItems.Should().ContainSingle(a => a.LineTotal == 300m);
        result.Value.AddOnTotal.Should().Be(300m);
        result.Value.TotalPayable.Should().Be(800m);
    }

    [Fact]
    public async Task An_addon_belonging_to_a_different_service_is_rejected()
    {
        using var context = _db.CreateContext();
        var (_, serviceOne, _, city) = SeedServiceAndCity(context, 500m);
        var category2 = new Category(Guid.NewGuid(), "Repairs", "repairs-" + Guid.NewGuid(), "desc");
        var serviceTwo = new Service(Guid.NewGuid(), category2.Id, "AC Repair", "ac-repair-" + Guid.NewGuid(), "desc", 400m);
        var foreignAddOn = new ServiceAddOn(Guid.NewGuid(), serviceTwo.Id, "Gas Refill", 200m);
        context.Add(category2);
        context.Add(serviceTwo);
        context.Add(foreignAddOn);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(
            new PriceCalculationRequest(serviceOne.Id, city.Id, 1, [new AddOnSelection(foreignAddOn.Id, 1)]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.InvalidAddOn");
    }

    [Fact]
    public async Task City_override_price_wins_over_the_services_base_price()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        context.ServiceCityPrices.Add(new ServiceCityPrice(Guid.NewGuid(), service.Id, city.Id, 650m));
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(service.Id, city.Id, 1, []));

        result.Value.BasePrice.Should().Be(650m);
    }

    [Fact]
    public async Task Visit_charge_tax_and_platform_fee_apply_on_top_of_the_subtotal()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        context.CityPricingPolicies.Add(new CityPricingPolicy(Guid.NewGuid(), city.Id, 50m, 18m, 10m));
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(service.Id, city.Id, 1, []));

        // subtotal = 500 (base) + 50 (visit) = 550; tax = 18% of 550 = 99; total = 550 + 99 + 10 = 659
        result.Value.Subtotal.Should().Be(550m);
        result.Value.TaxAmount.Should().Be(99m);
        result.Value.TotalPayable.Should().Be(659m);
    }

    [Fact]
    public async Task Zero_or_negative_quantity_is_rejected()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context);

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(service.Id, city.Id, 0, []));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.InvalidQuantity");
    }

    [Fact]
    public async Task Unknown_service_returns_not_found()
    {
        using var context = _db.CreateContext();
        var (_, _, _, city) = SeedServiceAndCity(context);

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(Guid.NewGuid(), city.Id, 1, []));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.ServiceNotFound");
    }

    [Fact]
    public async Task Unknown_city_returns_not_found()
    {
        using var context = _db.CreateContext();
        var (_, service, _, _) = SeedServiceAndCity(context);

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(service.Id, Guid.NewGuid(), 1, []));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.CityNotFound");
    }

    [Fact]
    public async Task Zero_or_negative_addon_quantity_is_rejected()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var addOn = new ServiceAddOn(Guid.NewGuid(), service.Id, "Sofa Cleaning", 150m);
        context.Add(addOn);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(
            new PriceCalculationRequest(service.Id, city.Id, 1, [new AddOnSelection(addOn.Id, 0)]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.InvalidAddOnQuantity");
    }

    [Fact]
    public async Task An_inactive_service_returns_not_found()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context);
        service.Deactivate();
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(service.Id, city.Id, 1, []));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.ServiceNotFound");
    }

    [Fact]
    public async Task An_inactive_addon_is_rejected_even_though_it_belongs_to_the_right_service()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var addOn = new ServiceAddOn(Guid.NewGuid(), service.Id, "Sofa Cleaning", 150m);
        addOn.Deactivate();
        context.Add(addOn);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(
            new PriceCalculationRequest(service.Id, city.Id, 1, [new AddOnSelection(addOn.Id, 1)]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.InvalidAddOn");
    }

    // ---- Phase 3 catalog redesign: variants + grouped add-ons ----

    [Fact]
    public async Task A_selected_variants_price_and_duration_win_over_the_services_flat_price()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var variant = new ServiceVariant(Guid.NewGuid(), service.Id, "Split AC", 799m, 90);
        context.Add(variant);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(
            new PriceCalculationRequest(service.Id, city.Id, 1, [], variant.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value.BasePrice.Should().Be(799m);
        result.Value.SelectedVariantId.Should().Be(variant.Id);
        result.Value.SelectedVariantName.Should().Be("Split AC");
        result.Value.SelectedVariantDurationMinutes.Should().Be(90);
    }

    [Fact]
    public async Task A_city_price_override_is_ignored_when_a_variant_is_selected()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var variant = new ServiceVariant(Guid.NewGuid(), service.Id, "Split AC", 799m, 90);
        context.Add(variant);
        context.ServiceCityPrices.Add(new ServiceCityPrice(Guid.NewGuid(), service.Id, city.Id, 650m));
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(
            new PriceCalculationRequest(service.Id, city.Id, 1, [], variant.Id));

        result.Value.BasePrice.Should().Be(799m);
    }

    [Fact]
    public async Task An_inactive_variant_is_rejected()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var variant = new ServiceVariant(Guid.NewGuid(), service.Id, "Split AC", 799m, 90);
        variant.Deactivate();
        context.Add(variant);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(
            new PriceCalculationRequest(service.Id, city.Id, 1, [], variant.Id));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.VariantNotFound");
    }

    [Fact]
    public async Task A_variant_belonging_to_a_different_service_is_rejected()
    {
        using var context = _db.CreateContext();
        var (_, serviceOne, _, city) = SeedServiceAndCity(context, 500m);
        var category2 = new Category(Guid.NewGuid(), "Repairs", "repairs-" + Guid.NewGuid(), "desc");
        var serviceTwo = new Service(Guid.NewGuid(), category2.Id, "AC Repair", "ac-repair-" + Guid.NewGuid(), "desc", 400m);
        var foreignVariant = new ServiceVariant(Guid.NewGuid(), serviceTwo.Id, "Window AC", 399m, 60);
        context.Add(category2);
        context.Add(serviceTwo);
        context.Add(foreignVariant);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(
            new PriceCalculationRequest(serviceOne.Id, city.Id, 1, [], foreignVariant.Id));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.VariantNotFound");
    }

    [Fact]
    public async Task A_request_with_no_variant_id_behaves_identically_to_before_variants_existed()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var variant = new ServiceVariant(Guid.NewGuid(), service.Id, "Split AC", 799m, 90);
        context.Add(variant);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(service.Id, city.Id, 2, []));

        result.IsSuccess.Should().BeTrue();
        result.Value.BasePrice.Should().Be(500m);
        result.Value.SelectedVariantId.Should().BeNull();
        result.Value.SelectedVariantName.Should().BeNull();
    }

    [Fact]
    public async Task Selecting_two_addons_from_a_single_selection_group_is_rejected()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var group = new ServiceAddOnGroup(Guid.NewGuid(), service.Id, "Detergent", AddOnGroupSelectionType.Single);
        var addOnA = new ServiceAddOn(Guid.NewGuid(), service.Id, "Powder", 50m);
        addOnA.SetGroupId(group.Id);
        var addOnB = new ServiceAddOn(Guid.NewGuid(), service.Id, "Liquid", 60m);
        addOnB.SetGroupId(group.Id);
        context.Add(group);
        context.Add(addOnA);
        context.Add(addOnB);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(
            service.Id, city.Id, 1, [new AddOnSelection(addOnA.Id, 1), new AddOnSelection(addOnB.Id, 1)]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.AddOnGroupSingleSelectionViolated");
    }

    [Fact]
    public async Task Selecting_more_addons_than_a_groups_max_select_is_rejected()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var group = new ServiceAddOnGroup(Guid.NewGuid(), service.Id, "Extras", AddOnGroupSelectionType.Multiple);
        group.SetSelectionRule(minSelect: 0, maxSelect: 1);
        var addOnA = new ServiceAddOn(Guid.NewGuid(), service.Id, "A", 50m);
        addOnA.SetGroupId(group.Id);
        var addOnB = new ServiceAddOn(Guid.NewGuid(), service.Id, "B", 60m);
        addOnB.SetGroupId(group.Id);
        context.Add(group);
        context.Add(addOnA);
        context.Add(addOnB);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(
            service.Id, city.Id, 1, [new AddOnSelection(addOnA.Id, 1), new AddOnSelection(addOnB.Id, 1)]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.AddOnGroupMaxSelectExceeded");
    }

    [Fact]
    public async Task Selecting_fewer_addons_than_a_groups_min_select_is_rejected()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var group = new ServiceAddOnGroup(Guid.NewGuid(), service.Id, "Required extras", AddOnGroupSelectionType.Multiple);
        group.SetSelectionRule(minSelect: 2, maxSelect: null);
        var addOn = new ServiceAddOn(Guid.NewGuid(), service.Id, "A", 50m);
        addOn.SetGroupId(group.Id);
        context.Add(group);
        context.Add(addOn);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(
            service.Id, city.Id, 1, [new AddOnSelection(addOn.Id, 1)]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Pricing.AddOnGroupMinSelectNotMet");
    }

    [Fact]
    public async Task A_valid_single_selection_from_a_group_is_priced_and_carries_the_group_name()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var group = new ServiceAddOnGroup(Guid.NewGuid(), service.Id, "Detergent", AddOnGroupSelectionType.Single);
        var addOn = new ServiceAddOn(Guid.NewGuid(), service.Id, "Powder", 50m);
        addOn.SetGroupId(group.Id);
        context.Add(group);
        context.Add(addOn);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(
            service.Id, city.Id, 1, [new AddOnSelection(addOn.Id, 1)]));

        result.IsSuccess.Should().BeTrue();
        var line = result.Value.AddOnLineItems.Should().ContainSingle().Subject;
        line.GroupId.Should().Be(group.Id);
        line.GroupName.Should().Be("Detergent");
    }

    [Fact]
    public async Task An_ungrouped_addon_selection_carries_no_group_info_same_as_before_groups_existed()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var addOn = new ServiceAddOn(Guid.NewGuid(), service.Id, "Standalone", 50m);
        context.Add(addOn);
        context.SaveChanges();

        var result = await BuildService(context).CalculateAsync(new PriceCalculationRequest(
            service.Id, city.Id, 1, [new AddOnSelection(addOn.Id, 1)]));

        var line = result.Value.AddOnLineItems.Should().ContainSingle().Subject;
        line.GroupId.Should().BeNull();
        line.GroupName.Should().BeNull();
    }

    /// <summary>
    /// Catalog/pricing caching fix: a second identical request is served from
    /// cache rather than recomputed - proven the same way ServiceQueryService's
    /// caching is proven elsewhere, by mutating the underlying price after the
    /// first call and asserting the second call still returns the pre-mutation
    /// value (only possible if it came from cache, not a fresh read).
    /// </summary>
    [Fact]
    public async Task Second_identical_request_is_served_from_cache_not_recomputed()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var priceService = BuildService(context);
        var request = new PriceCalculationRequest(service.Id, city.Id, 1, []);

        var first = await priceService.CalculateAsync(request);
        first.IsSuccess.Should().BeTrue();
        first.Value.BasePrice.Should().Be(500m);

        service.SetPrice(750m);
        context.SaveChanges();

        var second = await priceService.CalculateAsync(request);

        second.IsSuccess.Should().BeTrue();
        second.Value.BasePrice.Should().Be(500m, "the identical request should hit the cache instead of re-reading the now-changed price");
    }

    /// <summary>
    /// Requests that differ only in their add-on selection must not collide on
    /// one cache entry - each priced-out result is specific to exactly the
    /// add-ons requested.
    /// </summary>
    [Fact]
    public async Task Requests_with_different_addons_are_cached_under_different_keys()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var addOn = new ServiceAddOn(Guid.NewGuid(), service.Id, "Extra", 100m);
        context.Add(addOn);
        context.SaveChanges();
        var priceService = BuildService(context);

        var withoutAddOn = await priceService.CalculateAsync(new PriceCalculationRequest(service.Id, city.Id, 1, []));
        var withAddOn = await priceService.CalculateAsync(new PriceCalculationRequest(
            service.Id, city.Id, 1, [new AddOnSelection(addOn.Id, 1)]));

        withoutAddOn.IsSuccess.Should().BeTrue();
        withAddOn.IsSuccess.Should().BeTrue();
        withoutAddOn.Value.TotalPayable.Should().Be(500m);
        withAddOn.Value.TotalPayable.Should().Be(600m);
    }

    /// <summary>A failed calculation (here: quantity &lt;= 0) must never be cached - only the priced-out success case is.</summary>
    [Fact]
    public async Task A_failed_calculation_is_not_cached()
    {
        using var context = _db.CreateContext();
        var (_, service, _, city) = SeedServiceAndCity(context, 500m);
        var priceService = BuildService(context);
        var request = new PriceCalculationRequest(service.Id, city.Id, 0, []);

        var first = await priceService.CalculateAsync(request);
        first.IsFailure.Should().BeTrue();

        service.SetOptions(
            isTaxApplicable: true,
            isAddOnAllowed: true,
            isQuantityAllowed: true,
            isInspectionBased: false,
            isSlotRequired: true,
            isAddressRequired: true,
            isCustomerNoteAllowed: true);
        context.SaveChanges();

        // A valid quantity against the same service/city is a different
        // request (different key) and must compute fresh, not read whatever a
        // cached failure might have left behind.
        var second = await priceService.CalculateAsync(request with { Quantity = 1 });
        second.IsSuccess.Should().BeTrue();
    }
}
