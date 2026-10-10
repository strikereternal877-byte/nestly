using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Storage;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// docs/OPEN-FIXES-FEATURES.csv "Service to pincode mapping" - follow-up
/// review ("I think auto deactivating is required as well"): admin
/// suspend/delete on <see cref="ProviderManagementService"/> is one of the
/// three trigger points that must call
/// <c>IServiceabilityMappingManagementService.AutoDisableUnservedMappingsAsync</c>
/// (mirroring <c>ReactivateAsync</c>'s existing auto-enable call, task
/// a6866b4). No test file previously exercised
/// SuspendAsync/ReactivateAsync/DeleteAsync at all.
/// </summary>
public sealed class ProviderManagementServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    private static ProviderManagementService CreateService(NestlyDbContext context, IFileStorageService? fileStorageService = null) => new(
        new ProviderRepository(context),
        new ProviderKycDocumentRepository(context),
        new ProviderBackgroundCheckRepository(context),
        new ProviderBankAccountRepository(context),
        new BookingRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderEarningLedgerRepository(context),
        new ProviderCapacityRepository(context),
        new ProviderServiceAreaRepository(context),
        new ProviderSessionRepository(context),
        new ServiceabilityMappingManagementService(
            new CategoryCityMappingRepository(context), new ServicePincodeMappingRepository(context), new CategoryRepository(context),
            new CityRepository(context), new ServiceRepository(context), new PincodeRepository(context),
            TestServices.AuditLogWriter(context), TestServices.SystemSettings(context)),
        new ProviderAvailabilityWindowRepository(context),
        new ReviewRepository(context),
        new ProviderStatusHistoryRepository(context),
        TestServices.ProviderNotificationPublisher(context),
        fileStorageService ?? new NoOpFileStorageService(),
        NullLogger<ProviderManagementService>.Instance);

    private static ServiceabilityMappingManagementService CreateMappingService(NestlyDbContext context) => new(
        new CategoryCityMappingRepository(context), new ServicePincodeMappingRepository(context), new CategoryRepository(context),
        new CityRepository(context), new ServiceRepository(context), new PincodeRepository(context),
        TestServices.AuditLogWriter(context), TestServices.SystemSettings(context));

    /// <summary>
    /// Backdates a mapping's pending-auto-disable timer past the grace
    /// period directly in the database, via a context separate from the one
    /// under test - EF Core's identity map would otherwise keep serving the
    /// original context's own already-tracked (recent) copy of the row
    /// instead of this update. See
    /// <c>Nestly.Catalog.Tests.ServiceabilityMappingManagementServiceTests</c>'s
    /// identical helper for the same reasoning.
    /// </summary>
    private async Task BackdatePendingAutoDisableAsync(Guid serviceId, Guid pincodeId)
    {
        await using var backdateContext = _database.CreateContext();
        var mapping = await backdateContext.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        var pastCutoff = DateTime.UtcNow.AddMinutes(-(Nestly.Application.Serviceability.ServiceabilityAutoManagementDefaults.AutoDisableGracePeriodMinutes + 1));
        await backdateContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE service_pincode_mapping SET pending_auto_disable_since = {pastCutoff} WHERE id = {mapping.Id}");
    }

    /// <summary>Seeds an Active provider with skill + area coverage for one (service, pincode) pair, and the resulting active mapping.</summary>
    private async Task<(Guid ProviderId, Guid ServiceId, Guid PincodeId)> SeedSoleCoverageAsync(NestlyDbContext context)
    {
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, "560" + Guid.NewGuid().ToString("N")[..3]);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var catalogService = new Service(Guid.NewGuid(), category.Id, "Deep Cleaning", "deep-cleaning-" + Guid.NewGuid(), "desc", 999m);
        var mapping = new ServicePincodeMapping(Guid.NewGuid(), catalogService.Id, pincode.Id);

        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "9" + Guid.NewGuid().ToString("N")[..9]);
        provider.ChangeStatus(ProviderStatus.Active);

        context.AddRange(state, city, pincode, category, catalogService, mapping, provider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), provider.Id, category.Id, catalogService.Id));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), provider.Id, city.Id, pincodeId: pincode.Id));
        await context.SaveChangesAsync();

        return (provider.Id, catalogService.Id, pincode.Id);
    }

    /// <summary>
    /// Row 31, docs/OPEN-FIXES-FEATURES.csv: an admin-created provider must
    /// be immediately matchable too, not just a self-registered one.
    /// </summary>
    [Fact]
    public async Task CreateAsync_seeds_a_non_empty_default_weekly_availability()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).CreateAsync(
            new CreateProviderRequest("Ravi Kumar", "Ravi's Repairs", "+9198" + Guid.NewGuid().ToString("N")[..8], "ravi@example.com"));

        result.IsSuccess.Should().BeTrue();
        var windows = await new ProviderAvailabilityWindowRepository(context).GetByProviderAsync(result.Value.Id);
        windows.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SuspendAsync_auto_disables_a_mapping_that_loses_its_sole_coverage()
    {
        await using var context = _database.CreateContext();
        var (providerId, serviceId, pincodeId) = await SeedSoleCoverageAsync(context);

        var result = await CreateService(context).SuspendAsync(providerId, new SuspendProviderRequest("Policy violation"));
        result.IsSuccess.Should().BeTrue();

        // The grace period means SuspendAsync's own auto-disable call only
        // starts the pending timer - see AutoDisableUnservedMappingsAsync's
        // doc comment. This test is about the eventual outcome once that
        // timer has elapsed, so it fast-forwards past it and re-checks
        // through a fresh context/service (the original context's copy of
        // the mapping would otherwise still show its pre-backdate state).
        var pending = await context.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        pending.IsActive.Should().BeTrue("the mapping is only pending disable until the grace period elapses");

        await BackdatePendingAutoDisableAsync(serviceId, pincodeId);
        await using var freshContext = _database.CreateContext();
        (await CreateMappingService(freshContext).AutoDisableUnservedMappingsAsync(providerId)).Should().Be(1);

        var mapping = await freshContext.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        mapping.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_auto_disables_a_mapping_that_loses_its_sole_coverage()
    {
        await using var context = _database.CreateContext();
        var (providerId, serviceId, pincodeId) = await SeedSoleCoverageAsync(context);

        var result = await CreateService(context).DeleteAsync(providerId, new DeleteProviderRequest("Test deletion."));
        result.IsSuccess.Should().BeTrue();

        await BackdatePendingAutoDisableAsync(serviceId, pincodeId);
        await using var freshContext = _database.CreateContext();
        (await CreateMappingService(freshContext).AutoDisableUnservedMappingsAsync(providerId)).Should().Be(1);

        var mapping = await freshContext.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        mapping.IsActive.Should().BeFalse();
    }

    /// <summary>
    /// The right-to-erasure gap this covers: <c>Provider.SoftDelete</c>
    /// already anonymizes the DB row, but until this test's behaviour was
    /// added, the provider's profile photo and every KYC document they'd
    /// uploaded stayed live in storage forever - "deleted" in name only.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_purges_the_providers_photo_and_kyc_document_files_from_storage()
    {
        await using var context = _database.CreateContext();
        var (providerId, _, _) = await SeedSoleCoverageAsync(context);

        var provider = await context.Set<Provider>().SingleAsync(p => p.Id == providerId);
        provider.SubmitPhoto("https://example.test/storage/photo.jpg");
        var kycDocument = new ProviderKycDocument(Guid.NewGuid(), providerId, ProviderKycDocumentType.IdentityProof, "https://example.test/storage/id-proof.jpg");
        context.Add(kycDocument);
        await context.SaveChangesAsync();

        var recordingStorage = new RecordingFileStorageService();
        var result = await CreateService(context, recordingStorage).DeleteAsync(providerId, new DeleteProviderRequest("Test deletion."));

        result.IsSuccess.Should().BeTrue();
        recordingStorage.DeletedReferences.Should().BeEquivalentTo(
            "https://example.test/storage/photo.jpg",
            "https://example.test/storage/id-proof.jpg");

        var persistedDocument = await context.Set<ProviderKycDocument>().SingleAsync(d => d.Id == kycDocument.Id);
        persistedDocument.FileRef.Should().Be("[erased]", "the row is kept for audit history, but must stop pointing at a file that no longer exists");
    }

    /// <summary>
    /// Same right-to-erasure gap as the photo/KYC test above, for the
    /// provider's structured bank account details: a "deleted" account must
    /// not leave a live account number/IFSC sitting in the database.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_erases_the_providers_bank_account_details()
    {
        await using var context = _database.CreateContext();
        var (providerId, _, _) = await SeedSoleCoverageAsync(context);

        var bankAccount = new ProviderBankAccount(Guid.NewGuid(), providerId, "Test Provider", "123456789012", "HDFC0001234", "HDFC Bank");
        context.Add(bankAccount);
        await context.SaveChangesAsync();

        var result = await CreateService(context).DeleteAsync(providerId, new DeleteProviderRequest("Test deletion."));

        result.IsSuccess.Should().BeTrue();
        var persisted = await context.Set<ProviderBankAccount>().SingleAsync(b => b.Id == bankAccount.Id);
        persisted.AccountNumber.Should().Be("[erased]", "the row is kept for audit history, but must stop carrying a live account number");
        persisted.IfscCode.Should().Be("[erased]");
        persisted.AccountHolderName.Should().Be("[erased]");
        persisted.BankName.Should().Be("[erased]");
    }

    private sealed class RecordingFileStorageService : IFileStorageService
    {
        public List<string> DeletedReferences { get; } = new();

        public Task<string> SaveAsync(Stream content, string fileNameHint, string contentType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This spy is for DeleteAsync assertions only.");

        public Task DeleteAsync(string fileReference, CancellationToken cancellationToken = default)
        {
            DeletedReferences.Add(fileReference);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SuspendAsync_leaves_the_mapping_active_when_another_provider_still_covers_it()
    {
        await using var context = _database.CreateContext();
        var (providerId, serviceId, pincodeId) = await SeedSoleCoverageAsync(context);
        var service = await context.Set<Service>().SingleAsync(s => s.Id == serviceId);
        var pincode = await context.Set<Pincode>().SingleAsync(p => p.Id == pincodeId);

        var otherProvider = new Provider(Guid.NewGuid(), "Meena Iyer", "Meena's Services", ProviderType.Individual, "9" + Guid.NewGuid().ToString("N")[..9]);
        otherProvider.ChangeStatus(ProviderStatus.Active);
        context.Add(otherProvider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), otherProvider.Id, service.CategoryId, service.Id));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), otherProvider.Id, pincode.CityId, pincodeId: pincode.Id));
        await context.SaveChangesAsync();

        var result = await CreateService(context).SuspendAsync(providerId, new SuspendProviderRequest("Policy violation"));

        result.IsSuccess.Should().BeTrue();
        var mapping = await context.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        mapping.IsActive.Should().BeTrue("the other active provider's coverage still makes the pair bookable");
    }

    /// <summary>Idempotent: suspending an already-suspended trigger path a second time (e.g. via ReactivateAsync then SuspendAsync again) must not error or double-toggle.</summary>
    [Fact]
    public async Task SuspendAsync_then_reactivating_and_suspending_again_stays_idempotent()
    {
        await using var context = _database.CreateContext();
        var (providerId, serviceId, pincodeId) = await SeedSoleCoverageAsync(context);
        var management = CreateService(context);

        (await management.SuspendAsync(providerId, new SuspendProviderRequest("Policy violation"))).IsSuccess.Should().BeTrue();
        (await management.ReactivateAsync(providerId)).IsSuccess.Should().BeTrue();
        var afterReactivate = await context.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        afterReactivate.IsActive.Should().BeTrue("ReactivateAsync's auto-enable restores the mapping once the provider's coverage counts again");

        (await management.SuspendAsync(providerId, new SuspendProviderRequest("Policy violation again"))).IsSuccess.Should().BeTrue();

        // Still pending, not yet disabled - the grace period from this
        // second suspend has not elapsed. Fast-forward past it the same way
        // the other grace-period tests in this file do, then re-check.
        await BackdatePendingAutoDisableAsync(serviceId, pincodeId);
        await using var freshContext = _database.CreateContext();
        (await CreateMappingService(freshContext).AutoDisableUnservedMappingsAsync(providerId)).Should().Be(1);

        var final = await freshContext.Set<ServicePincodeMapping>().SingleAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);
        final.IsActive.Should().BeFalse();
    }

    /// <summary>
    /// Backdates a provider's <c>created_at</c> directly in the database, via
    /// a context separate from the one under test - same reasoning as
    /// <see cref="BackdatePendingAutoDisableAsync"/> above: EF Core's
    /// identity map would otherwise keep serving the original context's
    /// already-tracked (recent) copy of the row instead of this update.
    /// <see cref="Provider"/> stamps <c>CreatedAt</c> to
    /// <c>DateTime.UtcNow</c> in its constructor with no way to pass one in,
    /// so a cohort-of-a-different-day fixture has no route but a direct SQL
    /// update.
    /// </summary>
    private async Task BackdateCreatedAtAsync(Guid providerId, DateTime createdAtUtc)
    {
        await using var backdateContext = _database.CreateContext();
        await backdateContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE provider SET created_at = {createdAtUtc} WHERE id = {providerId}");
    }

    private static Provider NewProvider(ProviderOnboardingStatus onboardingStatus, ProviderStatus status)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "9" + Guid.NewGuid().ToString("N")[..9]);
        // Drive OnboardingStatus through its real transitions (Registered ->
        // ProfileCompleted -> KycSubmitted -> KycVerified -> Completed)
        // rather than reflection, so these fixtures never desync from what
        // Provider's own state machine actually allows.
        if (onboardingStatus is ProviderOnboardingStatus.ProfileCompleted or ProviderOnboardingStatus.KycSubmitted
            or ProviderOnboardingStatus.KycVerified or ProviderOnboardingStatus.Completed)
        {
            provider.UpdateProfile(provider.LegalName, provider.DisplayName, provider.Email);
        }

        if (onboardingStatus is ProviderOnboardingStatus.KycSubmitted or ProviderOnboardingStatus.KycVerified or ProviderOnboardingStatus.Completed)
        {
            provider.MarkKycSubmitted();
        }

        if (onboardingStatus is ProviderOnboardingStatus.KycVerified or ProviderOnboardingStatus.Completed)
        {
            provider.MarkKycVerified();
        }

        if (onboardingStatus == ProviderOnboardingStatus.Completed)
        {
            provider.MarkOnboardingCompleted();
        }

        provider.ChangeStatus(status);
        return provider;
    }

    /// <summary>
    /// Provider Onboarding Overview dashboard: the six funnel counts are
    /// cumulative as of the selected date (everyone registered on or before
    /// it, not just that single day - docs/OPEN-FIXES-FEATURES.csv "Provider
    /// Onboarding Overview"), each dimension independently - not a
    /// mutually-exclusive partition. Seeds one provider per bucket plus a
    /// same-day Registered provider and a provider created yesterday (must
    /// still count, unlike the old single-day behaviour), then asserts every
    /// count in one pass.
    /// </summary>
    [Fact]
    public async Task GetOnboardingOverviewAsync_counts_are_cumulative_as_of_date_per_stage()
    {
        await using var context = _database.CreateContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var registeredToday = NewProvider(ProviderOnboardingStatus.Registered, ProviderStatus.PendingVerification);
        var kycSubmittedToday = NewProvider(ProviderOnboardingStatus.KycSubmitted, ProviderStatus.PendingVerification);
        var kycVerifiedToday = NewProvider(ProviderOnboardingStatus.KycVerified, ProviderStatus.PendingVerification);
        var liveAndActiveToday = NewProvider(ProviderOnboardingStatus.Completed, ProviderStatus.Active);
        var liveAndActiveYesterday = NewProvider(ProviderOnboardingStatus.Completed, ProviderStatus.Active);

        context.AddRange(registeredToday, kycSubmittedToday, kycVerifiedToday, liveAndActiveToday, liveAndActiveYesterday);
        await context.SaveChangesAsync();
        await BackdateCreatedAtAsync(liveAndActiveYesterday.Id, DateTime.UtcNow.AddDays(-1));

        var result = await CreateService(context).GetOnboardingOverviewAsync(new AdminProviderOnboardingOverviewRequest(today));

        result.IsSuccess.Should().BeTrue();
        var overview = result.Value;
        overview.Date.Should().Be(today);
        overview.TotalOnboardingCount.Should().Be(5, "cumulative as of today includes yesterday's provider too");
        overview.DocumentVerificationCount.Should().Be(1);
        overview.VerifiedCount.Should().Be(1);
        overview.PendingCount.Should().Be(3, "Registered/KycSubmitted/KycVerified all leave ProviderStatus at PendingVerification");
        overview.LiveCount.Should().Be(2, "both the today and yesterday providers reached Completed");
        overview.ActiveCount.Should().Be(2, "both the today and yesterday providers are Active");
    }

    /// <summary>Defaults to today when no date is supplied (mirrors <c>GetFulfilmentBoardAsync</c>'s own default).</summary>
    [Fact]
    public async Task GetOnboardingOverviewAsync_defaults_to_today_when_no_date_given()
    {
        await using var context = _database.CreateContext();

        var result = await CreateService(context).GetOnboardingOverviewAsync(new AdminProviderOnboardingOverviewRequest(null));

        result.IsSuccess.Should().BeTrue();
        result.Value.Date.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    /// <summary>
    /// Provider Onboarding Overview dashboard's click-through: SearchAsync's
    /// new CreatedFromUtc/CreatedToUtc range must land exactly on the day's
    /// cohort a tile summarized, excluding a provider created outside it.
    /// </summary>
    [Fact]
    public async Task SearchAsync_filters_by_created_date_range()
    {
        await using var context = _database.CreateContext();
        var inRange = NewProvider(ProviderOnboardingStatus.Registered, ProviderStatus.PendingVerification);
        var outOfRange = NewProvider(ProviderOnboardingStatus.Registered, ProviderStatus.PendingVerification);
        context.AddRange(inRange, outOfRange);
        await context.SaveChangesAsync();
        await BackdateCreatedAtAsync(outOfRange.Id, DateTime.UtcNow.AddDays(-5));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startOfDayUtc = today.ToDateTime(TimeOnly.MinValue);
        var endOfDayUtc = startOfDayUtc.AddDays(1).AddTicks(-1);

        var result = await CreateService(context).SearchAsync(
            new ProviderSearchRequest(null, null, null, null, CreatedFromUtc: startOfDayUtc, CreatedToUtc: endOfDayUtc));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(p => p.Id == inRange.Id);
        result.Value.Items.Should().NotContain(p => p.Id == outOfRange.Id);
    }

    public void Dispose() => _database.Dispose();
}
