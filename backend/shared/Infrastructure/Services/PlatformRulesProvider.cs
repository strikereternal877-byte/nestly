using Microsoft.Extensions.Logging;
using Nestly.Application.Settings;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// <see cref="IPlatformRules"/> over the admin-editable settings store. Scoped, and each group is read once per scope and
/// remembered: the slot engine asks on every one of up to 31 dates in a range request, and the answer cannot change
/// underneath a single request in any way that matters.
/// </summary>
public sealed class PlatformRulesProvider : IPlatformRules
{
    private static readonly BookingSettingsValidator BookingValidator = new();
    private static readonly SlotSettingsValidator SlotValidator = new();
    private static readonly TaxSettingsValidator TaxValidator = new();
    private static readonly WalletSettingsValidator WalletValidator = new();
    private static readonly CouponSettingsValidator CouponValidator = new();

    private readonly ISystemSettingRepository _repository;
    private readonly ILogger<PlatformRulesProvider> _logger;

    private Task<BookingSettings?>? _booking;
    private Task<SlotSettings?>? _slot;
    private Task<TaxSettings?>? _tax;
    private Task<WalletSettings?>? _wallet;
    private Task<CouponSettings?>? _coupon;

    public PlatformRulesProvider(ISystemSettingRepository repository, ILogger<PlatformRulesProvider> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public Task<BookingSettings?> GetBookingAsync(CancellationToken cancellationToken = default) =>
        _booking ??= AdminSavedSettings.ReadAsync(_repository, SystemSettingGroups.Booking, BookingValidator, _logger, cancellationToken);

    public Task<SlotSettings?> GetSlotAsync(CancellationToken cancellationToken = default) =>
        _slot ??= AdminSavedSettings.ReadAsync(_repository, SystemSettingGroups.Slot, SlotValidator, _logger, cancellationToken);

    public Task<TaxSettings?> GetTaxAsync(CancellationToken cancellationToken = default) =>
        _tax ??= AdminSavedSettings.ReadAsync(_repository, SystemSettingGroups.Tax, TaxValidator, _logger, cancellationToken);

    public Task<WalletSettings?> GetWalletAsync(CancellationToken cancellationToken = default) =>
        _wallet ??= AdminSavedSettings.ReadAsync(_repository, SystemSettingGroups.Wallet, WalletValidator, _logger, cancellationToken);

    public Task<CouponSettings?> GetCouponAsync(CancellationToken cancellationToken = default) =>
        _coupon ??= AdminSavedSettings.ReadAsync(_repository, SystemSettingGroups.Coupon, CouponValidator, _logger, cancellationToken);
}
