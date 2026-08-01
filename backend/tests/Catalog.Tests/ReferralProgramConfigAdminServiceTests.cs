using FluentAssertions;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Referral;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers task 167's admin config GET/PUT (including the audit trail) and task 174's milestone admin CRUD.</summary>
public sealed class ReferralProgramConfigAdminServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;
    private readonly Guid _adminUserId = Guid.NewGuid();

    public ReferralProgramConfigAdminServiceTests(TestDatabase db) => _db = db;

    private ReferralProgramConfigAdminService BuildService(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new ReferralProgramConfigRepository(context),
        new ReferralMilestoneRepository(context),
        new AuditLogWriter(context, new StubAuditContextProvider(_adminUserId)));

    /// <summary>Single-row table (see ReferralProgramConfig's doc comment) - reset to one known row per test the same way ReferralQualificationAndRewardTests.SeedConfig does.</summary>
    private static ReferralProgramConfig SeedConfig(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        context.RemoveRange(context.ReferralProgramConfigs);
        context.SaveChanges();

        var config = new ReferralProgramConfig(
            Guid.NewGuid(), ReferralRewardType.WalletCredit, 100m, ReferralRewardType.WalletCredit, 50m,
            299m, 30, maxReferralsPerCustomer: null, isActive: true);
        context.Add(config);
        context.SaveChanges();
        return config;
    }

    private static void ClearMilestones(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        context.RemoveRange(context.Set<ReferralMilestoneAward>());
        context.RemoveRange(context.Set<ReferralMilestone>());
        context.SaveChanges();
    }

    [Fact]
    public async Task GetAsync_returns_the_seeded_config()
    {
        using var context = _db.CreateContext();
        SeedConfig(context);

        var result = await BuildService(context).GetAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.ReferrerRewardValue.Should().Be(100m);
        result.Value.RefereeRewardValue.Should().Be(50m);
    }

    [Fact]
    public async Task UpdateAsync_persists_the_new_values_and_writes_an_audit_entry()
    {
        using var context = _db.CreateContext();
        var config = SeedConfig(context);
        var service = BuildService(context);

        var request = new ReferralProgramConfigUpdateRequest(
            ReferralRewardType.Coupon, 200m, ReferralRewardType.WalletCredit, 75m, 499m, 45, 5, true);

        var result = await service.UpdateAsync(request, _adminUserId);

        result.IsSuccess.Should().BeTrue();
        result.Value.ReferrerRewardType.Should().Be(ReferralRewardType.Coupon);
        result.Value.ReferrerRewardValue.Should().Be(200m);
        result.Value.MaxReferralsPerCustomer.Should().Be(5);

        using var verifyContext = _db.CreateContext();
        var persisted = verifyContext.ReferralProgramConfigs.Single(c => c.Id == config.Id);
        persisted.ReferrerRewardValue.Should().Be(200m);
        persisted.UpdatedByAdminUserId.Should().Be(_adminUserId);

        var auditEntry = verifyContext.Set<AuditLog>()
            .Where(a => a.EntityName == "ReferralProgramConfig" && a.EntityId == config.Id.ToString())
            .OrderByDescending(a => a.OccurredOnUtc)
            .First();
        auditEntry.Action.Should().Be("Updated");
        auditEntry.NewValues.Should().Contain("200");
    }

    [Fact]
    public async Task UpdateAsync_rejects_an_invalid_reward_value()
    {
        using var context = _db.CreateContext();
        SeedConfig(context);
        var service = BuildService(context);

        var request = new ReferralProgramConfigUpdateRequest(
            ReferralRewardType.WalletCredit, -10m, ReferralRewardType.WalletCredit, 50m, 299m, 30, null, true);

        var result = await service.UpdateAsync(request, _adminUserId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("ReferralProgramConfig.Invalid");
    }

    [Fact]
    public async Task CreateMilestoneAsync_rejects_a_duplicate_threshold()
    {
        using var context = _db.CreateContext();
        ClearMilestones(context);
        var service = BuildService(context);

        var first = await service.CreateMilestoneAsync(new ReferralMilestoneCreateRequest(5, ReferralRewardType.WalletCredit, 250m));
        first.IsSuccess.Should().BeTrue();

        var duplicate = await service.CreateMilestoneAsync(new ReferralMilestoneCreateRequest(5, ReferralRewardType.Coupon, 100m));

        duplicate.IsSuccess.Should().BeFalse();
        duplicate.Error.Code.Should().Be("ReferralMilestone.DuplicateThreshold");
    }

    [Fact]
    public async Task SetMilestoneActiveAsync_toggles_and_ListMilestonesAsync_reflects_it()
    {
        using var context = _db.CreateContext();
        ClearMilestones(context);
        var service = BuildService(context);
        var created = await service.CreateMilestoneAsync(new ReferralMilestoneCreateRequest(10, ReferralRewardType.WalletCredit, 300m));

        var deactivated = await service.SetMilestoneActiveAsync(created.Value.Id, false);
        deactivated.IsSuccess.Should().BeTrue();
        deactivated.Value.IsActive.Should().BeFalse();

        var all = await service.ListMilestonesAsync();
        all.Single(m => m.Id == created.Value.Id).IsActive.Should().BeFalse();
    }

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        private readonly Guid? _actorId;

        public StubAuditContextProvider(Guid? actorId) => _actorId = actorId;

        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, _actorId, IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }
}
