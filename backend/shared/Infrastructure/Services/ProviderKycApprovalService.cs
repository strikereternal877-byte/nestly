using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Notifications;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Serviceability;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderKycApprovalService"/>
public class ProviderKycApprovalService : IProviderKycApprovalService
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderKycDocumentRepository _kycDocumentRepository;
    private readonly IProviderBackgroundCheckRepository _backgroundCheckRepository;
    private readonly IProviderBankAccountRepository _bankAccountRepository;
    private readonly IServiceabilityMappingManagementService _serviceabilityMappingManagementService;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly IProviderStatusHistoryRepository _statusHistoryRepository;
    private readonly IProviderNotificationPublisher _notificationPublisher;

    public ProviderKycApprovalService(
        IProviderRepository providerRepository,
        IProviderKycDocumentRepository kycDocumentRepository,
        IProviderBackgroundCheckRepository backgroundCheckRepository,
        IProviderBankAccountRepository bankAccountRepository,
        IServiceabilityMappingManagementService serviceabilityMappingManagementService,
        IProviderStatusHistoryRepository statusHistoryRepository,
        IProviderNotificationPublisher notificationPublisher,
        IAuditLogWriter auditLogWriter)
    {
        _providerRepository = providerRepository;
        _kycDocumentRepository = kycDocumentRepository;
        _backgroundCheckRepository = backgroundCheckRepository;
        _bankAccountRepository = bankAccountRepository;
        _serviceabilityMappingManagementService = serviceabilityMappingManagementService;
        _auditLogWriter = auditLogWriter;
        _statusHistoryRepository = statusHistoryRepository;
        _notificationPublisher = notificationPublisher;
    }

    public async Task<IReadOnlyList<ProviderKycDocumentQueueItemResponse>> ListPendingDocumentsAsync(CancellationToken cancellationToken = default)
    {
        var documents = await _kycDocumentRepository.ListPendingAsync(cancellationToken);
        if (documents.Count == 0)
        {
            return [];
        }

        var providerIds = documents.Select(d => d.ProviderId).Distinct().ToList();
        var namesById = await _providerRepository.GetDisplayNamesByIdsAsync(providerIds);

        return documents
            .Select(d => new ProviderKycDocumentQueueItemResponse(
                d.Id,
                d.ProviderId,
                namesById.TryGetValue(d.ProviderId, out var name) ? name : "(deleted provider)",
                d.DocType,
                d.DocNumber,
                d.FileRef,
                d.SubmittedAt))
            .ToList();
    }

    public async Task<Result<ProviderKycDocumentResponse>> ApproveDocumentAsync(Guid documentId, Guid adminUserId)
    {
        var document = await _kycDocumentRepository.GetByIdAsync(documentId);
        if (document is null)
        {
            return Error.NotFound("ProviderKycApproval.DocumentNotFound", "KYC document was not found.");
        }

        if (document.VerificationStatus != ProviderKycVerificationStatus.Pending)
        {
            return Error.Business("ProviderKycApproval.AlreadyReviewed", $"This document was already {document.VerificationStatus}.");
        }

        document.Approve(adminUserId);
        await _auditLogWriter.WriteAsync(new AuditEntry("ProviderKycDocument", document.Id.ToString(), "Approved"));
        await _kycDocumentRepository.UpdateAsync(document);

        // Advances onboarding to KycVerified the first time a document is
        // approved (idempotent - see Provider.MarkKycVerified).
        var provider = await _providerRepository.GetByIdAsync(document.ProviderId);
        if (provider is not null)
        {
            provider.MarkKycVerified();
            await _providerRepository.UpdateAsync(provider);
        }

        return ToResponse(document);
    }

    public async Task<Result<ProviderKycDocumentResponse>> RejectDocumentAsync(Guid documentId, Guid adminUserId, RejectProviderKycDocumentRequest request)
    {
        var document = await _kycDocumentRepository.GetByIdAsync(documentId);
        if (document is null)
        {
            return Error.NotFound("ProviderKycApproval.DocumentNotFound", "KYC document was not found.");
        }

        if (document.VerificationStatus != ProviderKycVerificationStatus.Pending)
        {
            return Error.Business("ProviderKycApproval.AlreadyReviewed", $"This document was already {document.VerificationStatus}.");
        }

        document.Reject(adminUserId, request.Reason);
        await _auditLogWriter.WriteAsync(new AuditEntry(
            "ProviderKycDocument", document.Id.ToString(), "Rejected", NewValues: request.Reason));
        await _kycDocumentRepository.UpdateAsync(document);

        await _notificationPublisher.NotifyAsync(
            document.ProviderId,
            ProviderNotificationType.KycRejected,
            "Document rejected",
            $"Your {document.DocType} document was rejected: {request.Reason}",
            deepLinkPath: "/profile");

        return ToResponse(document);
    }

    public async Task<Result<ProviderBackgroundCheckResponse>> RecordBackgroundCheckAsync(Guid providerId, Guid adminUserId, RecordBackgroundCheckRequest request)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Error.NotFound("ProviderKycApproval.ProviderNotFound", "Provider was not found.");
        }

        if (request.Status == ProviderBackgroundCheckStatus.Pending)
        {
            return Error.Validation("ProviderKycApproval.InvalidStatus", "A background check must be recorded with a final Passed/Failed outcome.");
        }

        var check = new ProviderBackgroundCheck(Guid.NewGuid(), providerId, request.Status, adminUserId, request.Notes);
        await _auditLogWriter.WriteAsync(new AuditEntry(
            "ProviderBackgroundCheck", check.Id.ToString(), check.Status.ToString(), NewValues: check.Notes));
        await _backgroundCheckRepository.AddAsync(check);

        return new ProviderBackgroundCheckResponse(check.Id, check.Status, check.CheckedBy, check.CheckedAt, check.Notes);
    }

    public async Task<Result<ProviderDetailResponse>> ActivateAsync(Guid providerId)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Error.NotFound("ProviderKycApproval.ProviderNotFound", "Provider was not found.");
        }

        if (provider.Status == ProviderStatus.Active)
        {
            return Error.Business("ProviderKycApproval.AlreadyActive", "This provider is already active.");
        }

        if (provider.Status != ProviderStatus.PendingVerification)
        {
            return Error.Business("ProviderKycApproval.InvalidStatus", $"A provider can only be activated from PendingVerification (current status: {provider.Status}).");
        }

        // Task 160's gate: both KYC approval and a passed background check
        // are required before a provider can go fully Active.
        if (provider.OnboardingStatus != ProviderOnboardingStatus.KycVerified && provider.OnboardingStatus != ProviderOnboardingStatus.Completed)
        {
            return Error.Business("ProviderKycApproval.KycNotVerified", "At least one KYC document must be approved before this provider can be activated.");
        }

        var latestCheck = await _backgroundCheckRepository.GetLatestByProviderAsync(providerId);
        if (latestCheck is null || latestCheck.Status != ProviderBackgroundCheckStatus.Passed)
        {
            return Error.Business("ProviderKycApproval.BackgroundCheckRequired", "A passed background check is required before this provider can be activated.");
        }

        provider.ChangeStatus(ProviderStatus.Active);
        provider.MarkOnboardingCompleted();
        await _auditLogWriter.WriteAsync(new AuditEntry("Provider", provider.Id.ToString(), "Activated"));
        await _providerRepository.UpdateAsync(provider);

        // Bug 3 auto-enable: skills/areas set earlier during onboarding
        // (while the provider was still PendingVerification, so they did not
        // count as coverage yet) become live coverage the moment the
        // provider goes Active here - catch up the pincode mappings now
        // rather than waiting for the provider to next touch their skills or
        // areas.
        await _serviceabilityMappingManagementService.AutoEnableProviderCoverageAsync(providerId);

        var documents = await _kycDocumentRepository.GetByProviderAsync(providerId);
        var backgroundChecks = await _backgroundCheckRepository.ListByProviderAsync(providerId);
        var statusHistory = await _statusHistoryRepository.ListByProviderAsync(providerId);
        var bankAccount = await _bankAccountRepository.GetByProviderIdAsync(providerId);

        return ProviderDetailMapper.ToDetailResponse(provider, documents, backgroundChecks, statusHistory, bankAccount);
    }

    private static ProviderKycDocumentResponse ToResponse(ProviderKycDocument document) => new(
        document.Id, document.DocType, document.DocNumber, document.FileRef,
        document.VerificationStatus, document.VerifiedBy, document.VerifiedAt, document.SubmittedAt,
        document.RejectionReason);
}
