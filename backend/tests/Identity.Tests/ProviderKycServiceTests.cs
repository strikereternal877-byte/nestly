using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nestly.Application.ProviderIdentity;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// KYC document submission and status lookup (task 146c, submission side
/// only - approval/rejection is task 150b, not exercised here).
/// </summary>
public class ProviderKycServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private Guid _providerId;

    public ProviderKycServiceTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        _providerId = provider.Id;
        context.Add(provider);
        context.SaveChanges();
    }

    private ProviderKycService CreateService(NestlyDbContext context) =>
        new(new ProviderRepository(context), new ProviderKycDocumentRepository(context));

    [Fact]
    public async Task SubmitDocumentAsync_stores_the_document_as_pending()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).SubmitDocumentAsync(
            new SubmitProviderKycDocumentRequest(_providerId, ProviderKycDocumentType.IdentityProof, "s3://kyc/doc1.pdf", "AB1234567"));

        result.IsSuccess.Should().BeTrue();
        result.Value.VerificationStatus.Should().Be(nameof(ProviderKycVerificationStatus.Pending));

        var stored = await context.Set<ProviderKycDocument>().SingleAsync();
        stored.ProviderId.Should().Be(_providerId);
        stored.DocType.Should().Be(ProviderKycDocumentType.IdentityProof);
        stored.VerifiedAt.Should().BeNull();
    }

    [Fact]
    public async Task SubmitDocumentAsync_advances_onboarding_status_to_kyc_submitted()
    {
        await using var context = _database.CreateContext();
        await CreateService(context).SubmitDocumentAsync(
            new SubmitProviderKycDocumentRequest(_providerId, ProviderKycDocumentType.AddressProof, "s3://kyc/doc2.pdf", null));

        var provider = await context.Set<Provider>().SingleAsync(p => p.Id == _providerId);
        provider.OnboardingStatus.Should().Be(ProviderOnboardingStatus.KycSubmitted);
    }

    [Fact]
    public async Task SubmitDocumentAsync_rejects_an_unknown_provider()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).SubmitDocumentAsync(
            new SubmitProviderKycDocumentRequest(Guid.NewGuid(), ProviderKycDocumentType.IdentityProof, "s3://kyc/doc.pdf", null));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderKyc.ProviderNotFound");
    }

    [Fact]
    public async Task GetStatusAsync_returns_every_submitted_document_with_the_onboarding_status()
    {
        await using var context = _database.CreateContext();
        var service = CreateService(context);
        await service.SubmitDocumentAsync(
            new SubmitProviderKycDocumentRequest(_providerId, ProviderKycDocumentType.IdentityProof, "s3://kyc/id.pdf", null));
        await service.SubmitDocumentAsync(
            new SubmitProviderKycDocumentRequest(_providerId, ProviderKycDocumentType.AddressProof, "s3://kyc/addr.pdf", null));

        var status = await service.GetStatusAsync(_providerId);

        status.IsSuccess.Should().BeTrue();
        status.Value.Documents.Should().HaveCount(2);
        status.Value.OnboardingStatus.Should().Be(nameof(ProviderOnboardingStatus.KycSubmitted));
    }

    [Fact]
    public async Task GetStatusAsync_rejects_an_unknown_provider()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).GetStatusAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderKyc.ProviderNotFound");
    }

    public void Dispose() => _database.Dispose();
}
