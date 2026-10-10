using System.Text.Json;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Settings;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Typed read/write access over the <see cref="SystemSetting"/> store (SRS
/// 12.19, tasks 131a-131h). Every group shares the same get/validate/audit/
/// persist pipeline (<see cref="GetGroupAsync{T}"/>/<see cref="UpdateGroupAsync{T}"/>) -
/// the public methods only supply the group key and JSON shape, keeping the
/// seven settings groups from turning into seven near-duplicate services.
/// </summary>
/// <remarks>
/// This service manages the admin-configurable value. For the cancellation and
/// reschedule groups, that value is what the engines enforce:
/// <c>CancellationService</c>, <c>RescheduleService</c> and the recurring-plan
/// card read them through <see cref="Nestly.Application.Settings.IBookingPolicyProvider"/>,
/// which uses a group's stored value once an admin has saved it (a seeded,
/// never-saved row is ignored, so the <c>CancellationPolicy</c> /
/// <c>ReschedulePolicy</c> configuration stays in force until then) and falls
/// back to configuration if the stored value is unusable. The booking, slot,
/// tax, wallet and coupon groups are read the same way through
/// <see cref="Nestly.Application.Settings.IPlatformRules"/> (nothing saved means
/// nothing changes); which of their fields an engine applies is listed in
/// docs/DEVOPS.md, "What every admin setting does".
/// </remarks>
public class SystemSettingsService : ISystemSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISystemSettingRepository _repository;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly IAuditContextProvider _auditContextProvider;

    public SystemSettingsService(
        ISystemSettingRepository repository,
        IAuditLogWriter auditLogWriter,
        IAuditContextProvider auditContextProvider)
    {
        _repository = repository;
        _auditLogWriter = auditLogWriter;
        _auditContextProvider = auditContextProvider;
    }

    public Task<Result<BookingSettings>> GetBookingSettingsAsync(CancellationToken cancellationToken = default) =>
        GetGroupAsync<BookingSettings>(SystemSettingGroups.Booking, cancellationToken);

    public Task<Result<BookingSettings>> UpdateBookingSettingsAsync(BookingSettings settings, CancellationToken cancellationToken = default) =>
        UpdateGroupAsync(SystemSettingGroups.Booking, settings, cancellationToken);

    public Task<Result<SlotSettings>> GetSlotSettingsAsync(CancellationToken cancellationToken = default) =>
        GetGroupAsync<SlotSettings>(SystemSettingGroups.Slot, cancellationToken);

    public Task<Result<SlotSettings>> UpdateSlotSettingsAsync(SlotSettings settings, CancellationToken cancellationToken = default) =>
        UpdateGroupAsync(SystemSettingGroups.Slot, settings, cancellationToken);

    public Task<Result<CancellationSettings>> GetCancellationSettingsAsync(CancellationToken cancellationToken = default) =>
        GetGroupAsync<CancellationSettings>(SystemSettingGroups.Cancellation, cancellationToken);

    public Task<Result<CancellationSettings>> UpdateCancellationSettingsAsync(CancellationSettings settings, CancellationToken cancellationToken = default) =>
        UpdateGroupAsync(SystemSettingGroups.Cancellation, settings, cancellationToken);

    public Task<Result<RescheduleSettings>> GetRescheduleSettingsAsync(CancellationToken cancellationToken = default) =>
        GetGroupAsync<RescheduleSettings>(SystemSettingGroups.Reschedule, cancellationToken);

    public Task<Result<RescheduleSettings>> UpdateRescheduleSettingsAsync(RescheduleSettings settings, CancellationToken cancellationToken = default) =>
        UpdateGroupAsync(SystemSettingGroups.Reschedule, settings, cancellationToken);

    public Task<Result<TaxSettings>> GetTaxSettingsAsync(CancellationToken cancellationToken = default) =>
        GetGroupAsync<TaxSettings>(SystemSettingGroups.Tax, cancellationToken);

    public Task<Result<TaxSettings>> UpdateTaxSettingsAsync(TaxSettings settings, CancellationToken cancellationToken = default) =>
        UpdateGroupAsync(SystemSettingGroups.Tax, settings, cancellationToken);

    public Task<Result<WalletSettings>> GetWalletSettingsAsync(CancellationToken cancellationToken = default) =>
        GetGroupAsync<WalletSettings>(SystemSettingGroups.Wallet, cancellationToken);

    public Task<Result<WalletSettings>> UpdateWalletSettingsAsync(WalletSettings settings, CancellationToken cancellationToken = default) =>
        UpdateGroupAsync(SystemSettingGroups.Wallet, settings, cancellationToken);

    public Task<Result<CouponSettings>> GetCouponSettingsAsync(CancellationToken cancellationToken = default) =>
        GetGroupAsync<CouponSettings>(SystemSettingGroups.Coupon, cancellationToken);

    public Task<Result<CouponSettings>> UpdateCouponSettingsAsync(CouponSettings settings, CancellationToken cancellationToken = default) =>
        UpdateGroupAsync(SystemSettingGroups.Coupon, settings, cancellationToken);

    public Task<Result<FeatureFlagSettings>> GetFeatureFlagSettingsAsync(CancellationToken cancellationToken = default) =>
        GetGroupAsync<FeatureFlagSettings>(SystemSettingGroups.Feature, cancellationToken);

    public Task<Result<FeatureFlagSettings>> UpdateFeatureFlagSettingsAsync(FeatureFlagSettings settings, CancellationToken cancellationToken = default) =>
        UpdateGroupAsync(SystemSettingGroups.Feature, settings, cancellationToken);

    public async Task<Result<AllSystemSettingsResponse>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SystemSetting> rows = await _repository.GetAllAsync(cancellationToken);
        var byGroup = rows.ToDictionary(r => r.GroupKey, r => r.ValueJson);

        if (!TryDeserializeAll(byGroup, out AllSystemSettingsResponse? response, out Error error))
        {
            return error;
        }

        return response!;
    }

    private async Task<Result<T>> GetGroupAsync<T>(string groupKey, CancellationToken cancellationToken)
    {
        SystemSetting? row = await _repository.GetByGroupKeyAsync(groupKey, cancellationToken);
        if (row is null)
        {
            return NotInitializedError(groupKey);
        }

        T? value = JsonSerializer.Deserialize<T>(row.ValueJson, JsonOptions);
        if (value is null)
        {
            return Error.Unexpected("Settings.Corrupt", $"Settings group '{groupKey}' could not be read - its stored value is invalid.");
        }

        return value;
    }

    private async Task<Result<T>> UpdateGroupAsync<T>(string groupKey, T settings, CancellationToken cancellationToken)
    {
        SystemSetting? row = await _repository.GetByGroupKeyAsync(groupKey, cancellationToken);
        if (row is null)
        {
            return NotInitializedError(groupKey);
        }

        string oldValueJson = row.ValueJson;
        string newValueJson = JsonSerializer.Serialize(settings, JsonOptions);

        Guid? actorId = _auditContextProvider.GetCurrent().ActorId;
        row.UpdateValue(newValueJson, actorId);

        // Staged into the same DbContext change tracker as row's own update -
        // UpdateAsync's SaveChangesAsync below commits both in one
        // transaction (IAuditLogWriter.WriteAsync's documented contract).
        await _auditLogWriter.WriteAsync(
            new AuditEntry("SystemSetting", groupKey, "Updated", oldValueJson, newValueJson),
            cancellationToken);

        await _repository.UpdateAsync(row, cancellationToken);

        return settings;
    }

    private static bool TryDeserializeAll(
        IReadOnlyDictionary<string, string> byGroup,
        out AllSystemSettingsResponse? response,
        out Error error)
    {
        response = null;

        foreach (string groupKey in SystemSettingGroups.All)
        {
            if (!byGroup.ContainsKey(groupKey))
            {
                error = NotInitializedError(groupKey);
                return false;
            }
        }

        error = Error.None;
        response = new AllSystemSettingsResponse(
            JsonSerializer.Deserialize<BookingSettings>(byGroup[SystemSettingGroups.Booking], JsonOptions)!,
            JsonSerializer.Deserialize<SlotSettings>(byGroup[SystemSettingGroups.Slot], JsonOptions)!,
            JsonSerializer.Deserialize<CancellationSettings>(byGroup[SystemSettingGroups.Cancellation], JsonOptions)!,
            JsonSerializer.Deserialize<RescheduleSettings>(byGroup[SystemSettingGroups.Reschedule], JsonOptions)!,
            JsonSerializer.Deserialize<TaxSettings>(byGroup[SystemSettingGroups.Tax], JsonOptions)!,
            JsonSerializer.Deserialize<WalletSettings>(byGroup[SystemSettingGroups.Wallet], JsonOptions)!,
            JsonSerializer.Deserialize<CouponSettings>(byGroup[SystemSettingGroups.Coupon], JsonOptions)!,
            JsonSerializer.Deserialize<FeatureFlagSettings>(byGroup[SystemSettingGroups.Feature], JsonOptions)!);
        return true;
    }

    private static Error NotInitializedError(string groupKey) =>
        Error.NotFound("Settings.GroupNotInitialized", $"Settings group '{groupKey}' has not been initialized.");
}
