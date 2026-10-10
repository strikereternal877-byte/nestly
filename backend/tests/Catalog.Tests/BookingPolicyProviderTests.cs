using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application.Settings;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Which cancellation / reschedule policy the engines enforce: what an admin saved in Settings, otherwise what the
/// deployment is configured with. The switch has to be safe to ship - a deployment that has never touched Settings keeps
/// enforcing exactly its configuration - and an unusable saved value must never take cancellations or reschedules down.
/// </summary>
public sealed class BookingPolicyProviderTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public BookingPolicyProviderTests(TestDatabase db) => _db = db;

    // What the deployment is configured with - deliberately not the defaults, so a fallback is distinguishable from one.
    private static readonly CancellationPolicyOptions ConfiguredCancellation =
        new() { FreeCancellationWindowHours = 24m, LateCancellationFeePercentage = 15m };

    private static readonly ReschedulePolicyOptions ConfiguredReschedule =
        new() { MinHoursBeforeSlot = 3m, MaxReschedulesPerBooking = 4, LateFeeThresholdHours = 12m, LateRescheduleFeePercentage = 8m, CollectLateFeeFromWallet = true };

    private const string SavedCancellation = "{\"freeCancellationWindowHours\":72,\"lateCancellationFeePercentage\":35,\"allowAdminOverride\":false}";
    private const string SavedReschedule = "{\"minHoursBeforeSlot\":1,\"maxReschedulesPerBooking\":9,\"lateFeeThresholdHours\":48,\"lateRescheduleFeePercentage\":25}";

    private static BookingPolicyProvider Build(ISystemSettingRepository repository) => new(
        repository,
        Options.Create(ConfiguredCancellation),
        Options.Create(ConfiguredReschedule),
        NullLogger<BookingPolicyProvider>.Instance);

    private static SystemSetting Row(string group, string json, bool savedByAdmin)
    {
        var row = new SystemSetting(Guid.NewGuid(), group, json);
        if (savedByAdmin)
        {
            row.UpdateValue(json, Guid.NewGuid());
        }

        return row;
    }

    [Fact]
    public async Task With_no_settings_at_all_the_configured_policy_is_enforced()
    {
        var provider = Build(new FakeSettings());

        var cancellation = await provider.GetCancellationAsync();
        cancellation.FreeCancellationWindowHours.Should().Be(24m);
        cancellation.LateCancellationFeePercentage.Should().Be(15m);

        var reschedule = await provider.GetRescheduleAsync();
        reschedule.MinHoursBeforeSlot.Should().Be(3m);
        reschedule.MaxReschedulesPerBooking.Should().Be(4);
        reschedule.LateFeeThresholdHours.Should().Be(12m);
        reschedule.LateRescheduleFeePercentage.Should().Be(8m);
    }

    /// <summary>
    /// The seeded row says nothing about what a deployment is configured to enforce. Treating it as the policy would silently
    /// change live bookings the moment this shipped.
    /// </summary>
    [Fact]
    public async Task Settings_nobody_has_saved_do_not_override_the_configured_policy()
    {
        var provider = Build(new FakeSettings(
            Row(SystemSettingGroups.Cancellation, SavedCancellation, savedByAdmin: false),
            Row(SystemSettingGroups.Reschedule, SavedReschedule, savedByAdmin: false)));

        (await provider.GetCancellationAsync()).FreeCancellationWindowHours.Should().Be(24m);
        (await provider.GetRescheduleAsync()).LateFeeThresholdHours.Should().Be(12m);
    }

    [Fact]
    public async Task Settings_an_admin_saved_are_the_policy()
    {
        var provider = Build(new FakeSettings(
            Row(SystemSettingGroups.Cancellation, SavedCancellation, savedByAdmin: true),
            Row(SystemSettingGroups.Reschedule, SavedReschedule, savedByAdmin: true)));

        var cancellation = await provider.GetCancellationAsync();
        cancellation.FreeCancellationWindowHours.Should().Be(72m);
        cancellation.LateCancellationFeePercentage.Should().Be(35m);
        cancellation.AllowAdminOverride.Should().BeFalse();

        var reschedule = await provider.GetRescheduleAsync();
        reschedule.MinHoursBeforeSlot.Should().Be(1m);
        reschedule.MaxReschedulesPerBooking.Should().Be(9);
        reschedule.LateFeeThresholdHours.Should().Be(48m);
        reschedule.LateRescheduleFeePercentage.Should().Be(25m);
    }

    [Fact]
    public async Task One_group_saved_does_not_move_the_other()
    {
        var provider = Build(new FakeSettings(Row(SystemSettingGroups.Cancellation, SavedCancellation, savedByAdmin: true)));

        (await provider.GetCancellationAsync()).FreeCancellationWindowHours.Should().Be(72m);
        (await provider.GetRescheduleAsync()).LateFeeThresholdHours.Should().Be(12m, "the reschedule group was never saved");
    }

    [Fact]
    public async Task Late_fee_collection_follows_configuration_until_an_admin_saves_and_an_older_save_reads_as_off()
    {
        // Nothing saved: the configured switch (on, in this suite's configuration) applies.
        (await Build(new FakeSettings()).GetRescheduleAsync()).CollectLateFeeFromWallet.Should().BeTrue();

        // Saved before the setting existed (no such field): reads as off, never as on by accident.
        var older = Build(new FakeSettings(Row(SystemSettingGroups.Reschedule, SavedReschedule, savedByAdmin: true)));
        (await older.GetRescheduleAsync()).CollectLateFeeFromWallet.Should().BeFalse("a value saved before collection existed did not choose it");

        // Saved with it switched on.
        const string savedOn = "{\"minHoursBeforeSlot\":1,\"maxReschedulesPerBooking\":9,\"lateFeeThresholdHours\":48,\"lateRescheduleFeePercentage\":25,\"collectLateFeeFromWallet\":true}";
        (await Build(new FakeSettings(Row(SystemSettingGroups.Reschedule, savedOn, savedByAdmin: true))).GetRescheduleAsync())
            .CollectLateFeeFromWallet.Should().BeTrue();
    }

    [Fact]
    public void Late_fee_collection_is_off_by_default_when_nothing_is_configured()
    {
        new ReschedulePolicyOptions().CollectLateFeeFromWallet.Should().BeFalse("production ships with collection off until it is switched on");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"freeCancellationWindowHours\":-5,\"lateCancellationFeePercentage\":20,\"allowAdminOverride\":true}")]
    [InlineData("{\"freeCancellationWindowHours\":4,\"lateCancellationFeePercentage\":150,\"allowAdminOverride\":true}")]
    public async Task A_saved_value_that_cannot_be_used_falls_back_to_the_configured_policy(string saved)
    {
        // "{}" deserializes to all-zero settings, which is a valid reading - only the rest are unreadable or out of range.
        var provider = Build(new FakeSettings(Row(SystemSettingGroups.Cancellation, saved, savedByAdmin: true)));

        var cancellation = await provider.GetCancellationAsync();

        if (saved == "{}")
        {
            cancellation.FreeCancellationWindowHours.Should().Be(0m);
        }
        else
        {
            cancellation.FreeCancellationWindowHours.Should().Be(24m);
            cancellation.LateCancellationFeePercentage.Should().Be(15m);
        }
    }

    [Fact]
    public async Task A_reschedule_value_out_of_range_falls_back_to_the_configured_policy()
    {
        var provider = Build(new FakeSettings(Row(
            SystemSettingGroups.Reschedule,
            "{\"minHoursBeforeSlot\":2,\"maxReschedulesPerBooking\":99,\"lateFeeThresholdHours\":6,\"lateRescheduleFeePercentage\":10}",
            savedByAdmin: true)));

        (await provider.GetRescheduleAsync()).MaxReschedulesPerBooking.Should().Be(4, "99 is above the 50 the Settings page itself allows");
    }

    /// <summary>The same behaviour through the real table and repository, not just a stand-in for them.</summary>
    [Fact]
    public async Task Through_the_real_settings_table_a_saved_value_wins_and_an_unsaved_one_does_not()
    {
        using (var context = _db.CreateContext())
        {
            var cancellation = new SystemSetting(Guid.NewGuid(), SystemSettingGroups.Cancellation, "{\"freeCancellationWindowHours\":4,\"lateCancellationFeePercentage\":20,\"allowAdminOverride\":true}");
            var reschedule = new SystemSetting(Guid.NewGuid(), SystemSettingGroups.Reschedule, "{\"minHoursBeforeSlot\":2,\"maxReschedulesPerBooking\":2,\"lateFeeThresholdHours\":6,\"lateRescheduleFeePercentage\":10}");
            context.Add(cancellation);
            context.Add(reschedule);
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            var provider = Build(new SystemSettingRepository(context));
            (await provider.GetCancellationAsync()).FreeCancellationWindowHours.Should().Be(24m, "the seeded row was never saved by an admin");
        }

        using (var context = _db.CreateContext())
        {
            var row = context.Set<SystemSetting>().Single(r => r.GroupKey == SystemSettingGroups.Cancellation);
            row.UpdateValue(SavedCancellation, Guid.NewGuid());
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            var provider = Build(new SystemSettingRepository(context));
            (await provider.GetCancellationAsync()).FreeCancellationWindowHours.Should().Be(72m, "now an admin has saved it");
            (await provider.GetRescheduleAsync()).LateFeeThresholdHours.Should().Be(12m, "the reschedule group is still unsaved");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // PlatformRulesProvider: the groups with no configuration fallback. null = not saved by an admin = engines do what they always did.
    // ---------------------------------------------------------------------------------------------------------------

    private static PlatformRulesProvider BuildRules(ISystemSettingRepository repository) =>
        new(repository, NullLogger<PlatformRulesProvider>.Instance);

    private const string SavedBooking = "{\"minLeadTimeHours\":6,\"maxAdvanceBookingDays\":14,\"maxActiveBookingsPerCustomer\":3,\"allowSameDayBooking\":false}";
    private const string SavedSlot = "{\"defaultSlotDurationMinutes\":90,\"sameDayCutoffHours\":4,\"maxAdvanceBookingDays\":10,\"defaultSlotCapacity\":2,\"allowOverbooking\":true}";
    private const string SavedTax = "{\"defaultTaxPercentage\":12,\"taxRegistrationNumber\":\"29ABCDE1234F1Z5\",\"taxInclusivePricing\":true}";
    private const string SavedWallet = "{\"maxWalletBalance\":9000,\"maxWalletUsagePercentagePerBooking\":60,\"walletCreditExpiryDays\":30,\"allowWalletTopUp\":false}";
    private const string SavedCoupon = "{\"maxDiscountPercentagePerCoupon\":25,\"maxActiveCouponsPerCustomer\":2,\"allowCouponStacking\":true,\"couponsEnabled\":false}";

    [Fact]
    public async Task Platform_rules_nobody_has_saved_are_null_even_though_the_seeded_rows_exist()
    {
        var rules = BuildRules(new FakeSettings(
            Row(SystemSettingGroups.Booking, SavedBooking, savedByAdmin: false),
            Row(SystemSettingGroups.Slot, SavedSlot, savedByAdmin: false),
            Row(SystemSettingGroups.Tax, SavedTax, savedByAdmin: false),
            Row(SystemSettingGroups.Wallet, SavedWallet, savedByAdmin: false),
            Row(SystemSettingGroups.Coupon, SavedCoupon, savedByAdmin: false)));

        (await rules.GetBookingAsync()).Should().BeNull("a seeded row is not a rule");
        (await rules.GetSlotAsync()).Should().BeNull();
        (await rules.GetTaxAsync()).Should().BeNull();
        (await rules.GetWalletAsync()).Should().BeNull();
        (await rules.GetCouponAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Platform_rules_an_admin_saved_come_through_group_by_group()
    {
        var rules = BuildRules(new FakeSettings(
            Row(SystemSettingGroups.Booking, SavedBooking, savedByAdmin: true),
            Row(SystemSettingGroups.Slot, SavedSlot, savedByAdmin: true),
            Row(SystemSettingGroups.Tax, SavedTax, savedByAdmin: true),
            Row(SystemSettingGroups.Wallet, SavedWallet, savedByAdmin: true),
            Row(SystemSettingGroups.Coupon, SavedCoupon, savedByAdmin: true)));

        var booking = (await rules.GetBookingAsync())!;
        booking.MinLeadTimeHours.Should().Be(6);
        booking.MaxAdvanceBookingDays.Should().Be(14);
        booking.MaxActiveBookingsPerCustomer.Should().Be(3);
        booking.AllowSameDayBooking.Should().BeFalse();

        var slot = (await rules.GetSlotAsync())!;
        slot.SameDayCutoffHours.Should().Be(4);
        slot.MaxAdvanceBookingDays.Should().Be(10);
        slot.AllowOverbooking.Should().BeTrue();

        (await rules.GetTaxAsync())!.DefaultTaxPercentage.Should().Be(12m);
        (await rules.GetWalletAsync())!.MaxWalletUsagePercentagePerBooking.Should().Be(60m);

        var coupon = (await rules.GetCouponAsync())!;
        coupon.MaxDiscountPercentagePerCoupon.Should().Be(25m);
        coupon.MaxActiveCouponsPerCustomer.Should().Be(2);
        coupon.CouponsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Saving_one_platform_group_leaves_the_others_unsaved()
    {
        var rules = BuildRules(new FakeSettings(
            Row(SystemSettingGroups.Tax, SavedTax, savedByAdmin: true),
            Row(SystemSettingGroups.Booking, SavedBooking, savedByAdmin: false)));

        (await rules.GetTaxAsync()).Should().NotBeNull();
        (await rules.GetBookingAsync()).Should().BeNull();
        (await rules.GetSlotAsync()).Should().BeNull("there is no row for it at all");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task A_platform_group_holding_an_unusable_value_is_treated_as_unsaved(string saved)
    {
        var rules = BuildRules(new FakeSettings(Row(SystemSettingGroups.Tax, saved, savedByAdmin: true)));

        (await rules.GetTaxAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_platform_group_holding_an_out_of_range_value_is_treated_as_unsaved()
    {
        var rules = BuildRules(new FakeSettings(
            Row(SystemSettingGroups.Tax, "{\"defaultTaxPercentage\":150,\"taxRegistrationNumber\":null,\"taxInclusivePricing\":false}", savedByAdmin: true)));

        (await rules.GetTaxAsync()).Should().BeNull("150% is not a tax rate the Settings page would have accepted");
    }

    [Fact]
    public async Task Each_platform_group_is_read_once_per_scope()
    {
        var store = new CountingSettings(Row(SystemSettingGroups.Slot, SavedSlot, savedByAdmin: true));
        var rules = BuildRules(store);

        // The slot engine asks on every one of up to 31 dates in a range request.
        for (int i = 0; i < 31; i++)
        {
            (await rules.GetSlotAsync()).Should().NotBeNull();
        }

        store.Reads.Should().Be(1);
    }

    private sealed class CountingSettings(params SystemSetting[] rows) : ISystemSettingRepository
    {
        public int Reads { get; private set; }

        public Task<SystemSetting?> GetByGroupKeyAsync(string groupKey, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(rows.FirstOrDefault(r => r.GroupKey == groupKey));
        }

        public Task<IReadOnlyList<SystemSetting>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SystemSetting>>(rows);

        public Task UpdateAsync(SystemSetting setting, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSettings(params SystemSetting[] rows) : ISystemSettingRepository
    {
        public Task<SystemSetting?> GetByGroupKeyAsync(string groupKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(rows.FirstOrDefault(r => r.GroupKey == groupKey));

        public Task<IReadOnlyList<SystemSetting>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SystemSetting>>(rows);

        public Task UpdateAsync(SystemSetting setting, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
