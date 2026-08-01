using System.Text.Json;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Referral;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>See <see cref="IReferralProgramConfigAdminService"/>.</summary>
public class ReferralProgramConfigAdminService : IReferralProgramConfigAdminService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IReferralProgramConfigRepository _configRepository;
    private readonly IReferralMilestoneRepository _milestoneRepository;
    private readonly IAuditLogWriter _auditLogWriter;

    public ReferralProgramConfigAdminService(
        IReferralProgramConfigRepository configRepository,
        IReferralMilestoneRepository milestoneRepository,
        IAuditLogWriter auditLogWriter)
    {
        _configRepository = configRepository;
        _milestoneRepository = milestoneRepository;
        _auditLogWriter = auditLogWriter;
    }

    public async Task<Result<ReferralProgramConfigResponse>> GetAsync()
    {
        var config = await _configRepository.GetAsync();
        if (config is null)
        {
            return Error.NotFound("ReferralProgramConfig.NotFound", "No referral program config exists yet.");
        }

        return Result.Success(ToResponse(config));
    }

    public async Task<Result<ReferralProgramConfigResponse>> UpdateAsync(ReferralProgramConfigUpdateRequest request, Guid adminUserId)
    {
        var config = await _configRepository.GetAsync();
        if (config is null)
        {
            return Error.NotFound("ReferralProgramConfig.NotFound", "No referral program config exists yet.");
        }

        string oldValueJson = JsonSerializer.Serialize(ToResponse(config), JsonOptions);

        try
        {
            config.Update(
                request.ReferrerRewardType,
                request.ReferrerRewardValue,
                request.RefereeRewardType,
                request.RefereeRewardValue,
                request.MinQualifyingOrderAmount,
                request.ReferralExpiryDays,
                request.MaxReferralsPerCustomer,
                request.IsActive,
                adminUserId);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Error.Validation("ReferralProgramConfig.Invalid", ex.Message);
        }

        string newValueJson = JsonSerializer.Serialize(ToResponse(config), JsonOptions);

        // Same "audit entry staged into the same unit of work the repository
        // commits" contract SystemSettingsService.UpdateGroupAsync relies on
        // (IAuditLogWriter.WriteAsync's documented contract) - one
        // SaveChangesAsync below commits both the config change and its
        // audit row together.
        await _auditLogWriter.WriteAsync(
            new AuditEntry("ReferralProgramConfig", config.Id.ToString(), "Updated", oldValueJson, newValueJson));

        await _configRepository.UpdateAsync(config);
        return Result.Success(ToResponse(config));
    }

    public async Task<IReadOnlyList<ReferralMilestoneResponse>> ListMilestonesAsync()
    {
        var milestones = await _milestoneRepository.ListAllOrderedByThresholdAsync();
        return milestones.Select(ToResponse).ToList();
    }

    public async Task<Result<ReferralMilestoneResponse>> CreateMilestoneAsync(ReferralMilestoneCreateRequest request)
    {
        if (await _milestoneRepository.ExistsByThresholdAsync(request.ThresholdCount))
        {
            return Error.Conflict("ReferralMilestone.DuplicateThreshold", $"A milestone for threshold {request.ThresholdCount} already exists.");
        }

        ReferralMilestone milestone;
        try
        {
            milestone = new ReferralMilestone(Guid.NewGuid(), request.ThresholdCount, request.BonusType, request.BonusValue, isActive: true);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Error.Validation("ReferralMilestone.Invalid", ex.Message);
        }

        await _milestoneRepository.AddAsync(milestone);
        return Result.Success(ToResponse(milestone));
    }

    public async Task<Result<ReferralMilestoneResponse>> SetMilestoneActiveAsync(Guid milestoneId, bool isActive)
    {
        var milestone = await _milestoneRepository.GetByIdAsync(milestoneId);
        if (milestone is null)
        {
            return Error.NotFound("ReferralMilestone.NotFound", "This milestone does not exist.");
        }

        milestone.SetActive(isActive);
        await _milestoneRepository.UpdateAsync(milestone);
        return Result.Success(ToResponse(milestone));
    }

    private static ReferralProgramConfigResponse ToResponse(ReferralProgramConfig config) => new(
        config.Id,
        config.ReferrerRewardType,
        config.ReferrerRewardValue,
        config.RefereeRewardType,
        config.RefereeRewardValue,
        config.MinQualifyingOrderAmount,
        config.ReferralExpiryDays,
        config.MaxReferralsPerCustomer,
        config.IsActive,
        config.UpdatedAtUtc);

    private static ReferralMilestoneResponse ToResponse(ReferralMilestone milestone) => new(
        milestone.Id,
        milestone.ThresholdCount,
        milestone.BonusType,
        milestone.BonusValue,
        milestone.IsActive,
        milestone.CreatedAtUtc);
}
