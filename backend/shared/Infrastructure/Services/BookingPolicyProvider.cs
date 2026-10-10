using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application.Settings;
using Nestly.Domain;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// <see cref="IBookingPolicyProvider"/> over the admin-editable <see cref="SystemSetting"/> store, falling back to the
/// configuration-bound <see cref="CancellationPolicyOptions"/> / <see cref="ReschedulePolicyOptions"/>.
///
/// <para>
/// A group counts as set by an admin when <see cref="SystemSetting.UpdatedByAdminUserId"/> is present - the seeded row
/// carries none, so a deployment that has never touched Settings keeps enforcing exactly what it enforced before this
/// provider existed. A stored value that cannot be read, or fails the same validator the Settings page applies, is
/// logged and ignored in favour of configuration rather than taking cancellations or reschedules down with it.
/// </para>
///
/// <para>
/// <c>CancellationSettings.AllowAdminOverride</c> comes from the stored value when there is one and is <c>true</c> from
/// configuration (the seeded default); nothing enforces it yet, so it has no effect either way.
/// </para>
/// </summary>
public sealed class BookingPolicyProvider : IBookingPolicyProvider
{
    private static readonly CancellationSettingsValidator CancellationValidator = new();
    private static readonly RescheduleSettingsValidator RescheduleValidator = new();

    private readonly ISystemSettingRepository _repository;
    private readonly CancellationPolicyOptions _cancellationFallback;
    private readonly ReschedulePolicyOptions _rescheduleFallback;
    private readonly ILogger<BookingPolicyProvider> _logger;

    public BookingPolicyProvider(
        ISystemSettingRepository repository,
        IOptions<CancellationPolicyOptions> cancellationFallback,
        IOptions<ReschedulePolicyOptions> rescheduleFallback,
        ILogger<BookingPolicyProvider> logger)
    {
        _repository = repository;
        _cancellationFallback = cancellationFallback.Value;
        _rescheduleFallback = rescheduleFallback.Value;
        _logger = logger;
    }

    public async Task<CancellationSettings> GetCancellationAsync(CancellationToken cancellationToken = default) =>
        await AdminSavedSettings.ReadAsync(_repository, SystemSettingGroups.Cancellation, CancellationValidator, _logger, cancellationToken)
        ?? new CancellationSettings(
            _cancellationFallback.FreeCancellationWindowHours,
            _cancellationFallback.LateCancellationFeePercentage,
            AllowAdminOverride: true);

    public async Task<RescheduleSettings> GetRescheduleAsync(CancellationToken cancellationToken = default) =>
        await AdminSavedSettings.ReadAsync(_repository, SystemSettingGroups.Reschedule, RescheduleValidator, _logger, cancellationToken)
        ?? new RescheduleSettings(
            _rescheduleFallback.MinHoursBeforeSlot,
            _rescheduleFallback.MaxReschedulesPerBooking,
            _rescheduleFallback.LateFeeThresholdHours,
            _rescheduleFallback.LateRescheduleFeePercentage,
            _rescheduleFallback.CollectLateFeeFromWallet);
}
