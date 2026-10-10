using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Nestly.Application.Settings;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// The one definition of "the value an admin saved for a settings group", shared by <see cref="BookingPolicyProvider"/> and
/// <see cref="PlatformRulesProvider"/> so the two cannot disagree about what counts as saved.
///
/// <para>
/// A group counts as saved when <see cref="SystemSetting.UpdatedByAdminUserId"/> is present - the seeded row carries none.
/// A stored value that cannot be read, is empty, or fails the validator the Settings page itself applies is logged and
/// reported as "not saved", so an unusable value can never take bookings down: the caller falls back to what it did before.
/// </para>
/// </summary>
internal static class AdminSavedSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<T?> ReadAsync<T>(
        ISystemSettingRepository repository, string groupKey, IValidator<T> validator, ILogger logger, CancellationToken cancellationToken)
        where T : class
    {
        SystemSetting? row = await repository.GetByGroupKeyAsync(groupKey, cancellationToken);
        if (row?.UpdatedByAdminUserId is null)
        {
            return null;
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(row.ValueJson, JsonOptions);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Settings group {GroupKey} holds an unreadable value; it is being ignored.", groupKey);
            return null;
        }

        if (value is null)
        {
            logger.LogError("Settings group {GroupKey} holds an empty value; it is being ignored.", groupKey);
            return null;
        }

        var validation = validator.Validate(value);
        if (!validation.IsValid)
        {
            logger.LogError(
                "Settings group {GroupKey} holds an invalid value ({Problems}); it is being ignored.",
                groupKey, string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
            return null;
        }

        return value;
    }
}
