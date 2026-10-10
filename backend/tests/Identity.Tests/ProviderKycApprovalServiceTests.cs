using System.Threading;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nestly.Application.ProviderIdentity;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// The admin KYC verification queue (<see cref="ProviderKycApprovalService.ListPendingDocumentsAsync"/>,
/// new admin-web "Verification queue" page) - approval/rejection itself is
/// exercised end-to-end via the admin-web UI and controller, not re-covered
/// here.
/// </summary>
public sealed class ProviderKycApprovalServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    private static ProviderKycApprovalService CreateApprovalService(NestlyDbContext context) => new(
        new ProviderRepository(context),
        new ProviderKycDocumentRepository(context),
        new ProviderBackgroundCheckRepository(context),
        new ProviderBankAccountRepository(context),
        new ServiceabilityMappingManagementService(
            new CategoryCityMappingRepository(context), new ServicePincodeMappingRepository(context), new CategoryRepository(context),
            new CityRepository(context), new ServiceRepository(context), new PincodeRepository(context),
            TestServices.AuditLogWriter(context), TestServices.SystemSettings(context)),
        new ProviderStatusHistoryRepository(context),
        TestServices.ProviderNotificationPublisher(context),
        TestServices.AuditLogWriter(context));

    private static ProviderKycService CreateKycService(NestlyDbContext context) =>
        new(new ProviderRepository(context), new ProviderKycDocumentRepository(context));

    private static int _phoneSequence;

    private static Provider NewProvider(string name) =>
        new(Guid.NewGuid(), name, name, ProviderType.Individual, $"+9198765{Interlocked.Increment(ref _phoneSequence):D5}");

    private async Task BackdateSubmittedAtAsync(Guid documentId, DateTime submittedAtUtc)
    {
        await using var backdateContext = _database.CreateContext();
        await backdateContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE provider_kyc_document SET submitted_at = {submittedAtUtc} WHERE id = {documentId}");
    }

    [Fact]
    public async Task ListPendingDocumentsAsync_returns_only_pending_documents_oldest_first()
    {
        var older = NewProvider("Older Provider");
        var newer = NewProvider("Newer Provider");
        var reviewed = NewProvider("Already Reviewed Provider");
        await using (var setupContext = _database.CreateContext())
        {
            setupContext.AddRange(older, newer, reviewed);
            await setupContext.SaveChangesAsync();
        }

        Guid olderDocId, newerDocId;
        await using (var context = _database.CreateContext())
        {
            var kycService = CreateKycService(context);
            var newerDoc = await kycService.SubmitDocumentAsync(
                new SubmitProviderKycDocumentRequest(newer.Id, ProviderKycDocumentType.IdentityProof, "s3://kyc/newer.pdf", null));
            var olderDoc = await kycService.SubmitDocumentAsync(
                new SubmitProviderKycDocumentRequest(older.Id, ProviderKycDocumentType.IdentityProof, "s3://kyc/older.pdf", null));
            var reviewedDoc = await kycService.SubmitDocumentAsync(
                new SubmitProviderKycDocumentRequest(reviewed.Id, ProviderKycDocumentType.IdentityProof, "s3://kyc/reviewed.pdf", null));
            newerDocId = newerDoc.Value.Id;
            olderDocId = olderDoc.Value.Id;

            await CreateApprovalService(context).ApproveDocumentAsync(reviewedDoc.Value.Id, Guid.NewGuid());
        }

        await BackdateSubmittedAtAsync(olderDocId, DateTime.UtcNow.AddDays(-2));
        await BackdateSubmittedAtAsync(newerDocId, DateTime.UtcNow.AddDays(-1));

        await using var queryContext = _database.CreateContext();
        var queue = await CreateApprovalService(queryContext).ListPendingDocumentsAsync();

        queue.Select(item => item.Id).Should().Equal(olderDocId, newerDocId);
        queue.Should().ContainSingle(item => item.Id == olderDocId && item.ProviderDisplayName == older.DisplayName);
        queue.Should().ContainSingle(item => item.Id == newerDocId && item.ProviderDisplayName == newer.DisplayName);
        queue.Select(item => item.ProviderId).Should().NotContain(reviewed.Id);
    }

    [Fact]
    public async Task ListPendingDocumentsAsync_returns_an_empty_list_when_nothing_is_pending()
    {
        await using var context = _database.CreateContext();
        var queue = await CreateApprovalService(context).ListPendingDocumentsAsync();

        queue.Should().BeEmpty();
    }

    public void Dispose() => _database.Dispose();
}
