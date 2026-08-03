using Nestly.Application;
using Nestly.Application.ProviderIdentity;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// KYC document submission and status lookup (task 146c). Only the
/// submission side - approval/rejection is task 150b's admin workflow,
/// which will call <see cref="ProviderKycDocument.Approve"/>/
/// <see cref="ProviderKycDocument.Reject"/> directly.
/// </summary>
public class ProviderKycService : IProviderKycService
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderKycDocumentRepository _kycDocumentRepository;

    public ProviderKycService(IProviderRepository providerRepository, IProviderKycDocumentRepository kycDocumentRepository)
    {
        _providerRepository = providerRepository;
        _kycDocumentRepository = kycDocumentRepository;
    }

    public async Task<Result<ProviderKycDocumentResponse>> SubmitDocumentAsync(SubmitProviderKycDocumentRequest request)
    {
        var provider = await _providerRepository.GetByIdAsync(request.ProviderId);
        if (provider is null)
        {
            return Result.Failure<ProviderKycDocumentResponse>(
                Error.NotFound("ProviderKyc.ProviderNotFound", "No provider found for this id."));
        }

        var document = new ProviderKycDocument(
            Guid.NewGuid(), request.ProviderId, request.DocType, request.FileRef, request.DocNumber);
        await _kycDocumentRepository.AddAsync(document);

        // Advances onboarding to KycSubmitted the first time a document is
        // submitted (idempotent - see Provider.MarkKycSubmitted).
        provider.MarkKycSubmitted();
        await _providerRepository.UpdateAsync(provider);

        return Result.Success(ToResponse(document));
    }

    public async Task<Result<ProviderKycStatusResponse>> GetStatusAsync(Guid providerId)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Result.Failure<ProviderKycStatusResponse>(
                Error.NotFound("ProviderKyc.ProviderNotFound", "No provider found for this id."));
        }

        var documents = await _kycDocumentRepository.GetByProviderAsync(providerId);

        return Result.Success(new ProviderKycStatusResponse(
            providerId,
            provider.OnboardingStatus.ToString(),
            documents.Select(ToResponse).ToList()));
    }

    private static ProviderKycDocumentResponse ToResponse(ProviderKycDocument document) => new(
        document.Id,
        document.ProviderId,
        document.DocType.ToString(),
        document.DocNumber,
        document.FileRef,
        document.VerificationStatus.ToString(),
        document.SubmittedAt,
        document.VerifiedAt);
}
