using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Notifications;
using Nestly.Application.ProviderManagement;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderBankAccountService"/>
/// <remarks>
/// Writes an audit entry for every submit/approve/reject (financial PII -
/// same "every write is audited" reasoning <see cref="ProviderPayoutService"/>'s
/// own doc comment gives for payout batches). Staged before the repository
/// call so the repository's own <c>SaveChangesAsync</c> commits both in one
/// transaction, matching <see cref="ProviderPayoutService"/>'s pattern
/// exactly.
/// </remarks>
public class ProviderBankAccountService : IProviderBankAccountService
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderBankAccountRepository _bankAccountRepository;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly IProviderNotificationPublisher _notificationPublisher;

    public ProviderBankAccountService(
        IProviderRepository providerRepository,
        IProviderBankAccountRepository bankAccountRepository,
        IAuditLogWriter auditLogWriter,
        IProviderNotificationPublisher notificationPublisher)
    {
        _providerRepository = providerRepository;
        _bankAccountRepository = bankAccountRepository;
        _auditLogWriter = auditLogWriter;
        _notificationPublisher = notificationPublisher;
    }

    public async Task<Result<ProviderBankAccountResponse>> SubmitAsync(SubmitProviderBankAccountRequest request)
    {
        if (!await _providerRepository.ExistsAsync(request.ProviderId))
        {
            return Error.NotFound("ProviderBankAccount.ProviderNotFound", "Provider was not found.");
        }

        var existing = await _bankAccountRepository.GetByProviderIdAsync(request.ProviderId);

        if (existing is null)
        {
            var created = new ProviderBankAccount(
                Guid.NewGuid(), request.ProviderId, request.AccountHolderName, request.AccountNumber, request.IfscCode, request.BankName);

            await _auditLogWriter.WriteAsync(new AuditEntry(
                "ProviderBankAccount",
                created.Id.ToString(),
                "Created",
                NewValues: $"ProviderId={created.ProviderId}; BankName={created.BankName}; IfscCode={created.IfscCode}; AccountNumber={Mask(created.AccountNumber)}"));

            await _bankAccountRepository.AddAsync(created);

            return ToResponse(created);
        }

        // Upsert - a resubmission always updates the same row and resets it
        // to Pending (see ProviderBankAccount.UpdateDetails), never creates a
        // second row.
        existing.UpdateDetails(request.AccountHolderName, request.AccountNumber, request.IfscCode, request.BankName);

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "ProviderBankAccount",
            existing.Id.ToString(),
            "Updated",
            NewValues: $"ProviderId={existing.ProviderId}; BankName={existing.BankName}; IfscCode={existing.IfscCode}; AccountNumber={Mask(existing.AccountNumber)}; Status=(reset)->Pending"));

        await _bankAccountRepository.UpdateAsync(existing);

        return ToResponse(existing);
    }

    public async Task<Result<ProviderBankAccountResponse>> GetAsync(Guid providerId)
    {
        var bankAccount = await _bankAccountRepository.GetByProviderIdAsync(providerId);
        if (bankAccount is null)
        {
            return Error.NotFound("ProviderBankAccount.NotFound", "No bank account details have been submitted yet.");
        }

        return ToResponse(bankAccount);
    }

    public async Task<IReadOnlyList<ProviderBankAccountQueueItemResponse>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _bankAccountRepository.ListPendingAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return [];
        }

        var providerIds = pending.Select(x => x.ProviderId).Distinct().ToList();
        var namesById = await _providerRepository.GetDisplayNamesByIdsAsync(providerIds);

        return pending
            .Select(x => new ProviderBankAccountQueueItemResponse(
                x.Id,
                x.ProviderId,
                namesById.TryGetValue(x.ProviderId, out var name) ? name : "(deleted provider)",
                x.AccountHolderName,
                Mask(x.AccountNumber),
                x.IfscCode,
                x.BankName,
                x.UpdatedAt))
            .ToList();
    }

    public async Task<Result<ProviderBankAccountResponse>> ApproveAsync(Guid bankAccountId, Guid adminUserId)
    {
        var bankAccount = await _bankAccountRepository.GetByIdAsync(bankAccountId);
        if (bankAccount is null)
        {
            return Error.NotFound("ProviderBankAccount.NotFound", "Bank account details were not found.");
        }

        if (bankAccount.VerificationStatus != ProviderBankAccountVerificationStatus.Pending)
        {
            return Error.Business("ProviderBankAccount.AlreadyReviewed", $"These bank account details were already {bankAccount.VerificationStatus}.");
        }

        bankAccount.Approve(adminUserId);

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "ProviderBankAccount",
            bankAccount.Id.ToString(),
            "Approved",
            NewValues: $"ProviderId={bankAccount.ProviderId}; Status=Pending->Verified"));

        await _bankAccountRepository.UpdateAsync(bankAccount);

        await _notificationPublisher.NotifyAsync(
            bankAccount.ProviderId,
            ProviderNotificationType.BankAccountApproved,
            "Bank account verified",
            "Your bank account details have been verified and are ready for payouts.",
            deepLinkPath: "/profile");

        return ToResponse(bankAccount);
    }

    public async Task<Result<ProviderBankAccountResponse>> RejectAsync(Guid bankAccountId, Guid adminUserId, RejectProviderBankAccountRequest request)
    {
        var bankAccount = await _bankAccountRepository.GetByIdAsync(bankAccountId);
        if (bankAccount is null)
        {
            return Error.NotFound("ProviderBankAccount.NotFound", "Bank account details were not found.");
        }

        if (bankAccount.VerificationStatus != ProviderBankAccountVerificationStatus.Pending)
        {
            return Error.Business("ProviderBankAccount.AlreadyReviewed", $"These bank account details were already {bankAccount.VerificationStatus}.");
        }

        bankAccount.Reject(adminUserId, request.Reason);

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "ProviderBankAccount",
            bankAccount.Id.ToString(),
            "Rejected",
            NewValues: $"ProviderId={bankAccount.ProviderId}; Status=Pending->Rejected; Reason={request.Reason}"));

        await _bankAccountRepository.UpdateAsync(bankAccount);

        await _notificationPublisher.NotifyAsync(
            bankAccount.ProviderId,
            ProviderNotificationType.BankAccountRejected,
            "Bank account rejected",
            $"Your bank account details were rejected: {request.Reason}",
            deepLinkPath: "/profile");

        return ToResponse(bankAccount);
    }

    /// <summary>Last-4-digits mask for anything that must not carry a full account number in bulk view - the audit trail and the admin queue row (see <see cref="ProviderBankAccountQueueItemResponse"/>'s doc comment).</summary>
    private static string Mask(string accountNumber) =>
        accountNumber.Length <= 4 ? accountNumber : $"••••{accountNumber[^4..]}";

    private static ProviderBankAccountResponse ToResponse(ProviderBankAccount bankAccount) => new(
        bankAccount.Id,
        bankAccount.ProviderId,
        bankAccount.AccountHolderName,
        bankAccount.AccountNumber,
        bankAccount.IfscCode,
        bankAccount.BankName,
        bankAccount.VerificationStatus,
        bankAccount.VerifiedBy,
        bankAccount.VerifiedAt,
        bankAccount.RejectionReason,
        bankAccount.UpdatedAt);
}
