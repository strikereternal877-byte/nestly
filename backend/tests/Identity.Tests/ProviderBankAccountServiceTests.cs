using System.Threading;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Structured, admin-verifiable provider bank account details (docs/PROVIDER.md
/// OPEN DECISIONS #3) - <see cref="ProviderBankAccountService"/>'s upsert
/// semantics (one row per provider, always reset to Pending on resubmission)
/// and its admin approve/reject lifecycle. Mirrors
/// <see cref="ProviderKycApprovalServiceTests"/>/<c>ProviderEarningsServiceTests</c>'
/// construction style (real repositories over a fresh in-memory-backed
/// <see cref="TestDatabase"/> context, not mocks).
/// </summary>
public sealed class ProviderBankAccountServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private static int _phoneSequence;

    private static ProviderBankAccountService CreateService(NestlyDbContext context) => new(
        new ProviderRepository(context),
        new ProviderBankAccountRepository(context),
        TestServices.AuditLogWriter(context),
        TestServices.ProviderNotificationPublisher(context));

    private static Provider NewProvider(string name) =>
        new(Guid.NewGuid(), name, name, ProviderType.Individual, $"+9198765{Interlocked.Increment(ref _phoneSequence):D5}");

    private async Task<Provider> SeedProviderAsync(string name = "Test Provider")
    {
        var provider = NewProvider(name);
        await using var context = _database.CreateContext();
        context.Add(provider);
        await context.SaveChangesAsync();
        return provider;
    }

    private static SubmitProviderBankAccountRequest ValidRequest(Guid providerId, string accountNumber = "123456789012") =>
        new(providerId, "Ravi Kumar", accountNumber, "HDFC0001234", "HDFC Bank");

    [Fact]
    public async Task SubmitAsync_creates_a_new_row_when_none_exists_yet()
    {
        var provider = await SeedProviderAsync();

        await using var context = _database.CreateContext();
        var result = await CreateService(context).SubmitAsync(ValidRequest(provider.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value.ProviderId.Should().Be(provider.Id);
        result.Value.AccountHolderName.Should().Be("Ravi Kumar");
        result.Value.AccountNumber.Should().Be("123456789012");
        result.Value.IfscCode.Should().Be("HDFC0001234");
        result.Value.BankName.Should().Be("HDFC Bank");
        result.Value.VerificationStatus.Should().Be(ProviderBankAccountVerificationStatus.Pending);
        result.Value.VerifiedBy.Should().BeNull();
        result.Value.VerifiedAt.Should().BeNull();
    }

    [Fact]
    public async Task SubmitAsync_a_second_time_updates_the_same_row_and_resets_it_to_Pending()
    {
        var provider = await SeedProviderAsync();

        Guid firstId;
        await using (var context = _database.CreateContext())
        {
            var service = CreateService(context);
            var first = await service.SubmitAsync(ValidRequest(provider.Id, "111111111111"));
            first.IsSuccess.Should().BeTrue();
            firstId = first.Value.Id;

            // Get it approved, so the resubmission below has to prove it
            // resets a VERIFIED row back to Pending, not just a fresh one.
            var approved = await service.ApproveAsync(firstId, Guid.NewGuid());
            approved.IsSuccess.Should().BeTrue();
        }

        await using (var context = _database.CreateContext())
        {
            var second = await CreateService(context).SubmitAsync(ValidRequest(provider.Id, "222222222222"));

            second.IsSuccess.Should().BeTrue();
            // Same row, not a new one - upsert, not append (unlike KYC documents).
            second.Value.Id.Should().Be(firstId);
            second.Value.AccountNumber.Should().Be("222222222222");
            second.Value.VerificationStatus.Should().Be(ProviderBankAccountVerificationStatus.Pending);
            second.Value.VerifiedBy.Should().BeNull();
            second.Value.VerifiedAt.Should().BeNull();
            second.Value.RejectionReason.Should().BeNull();
        }

        await using var verifyContext = _database.CreateContext();
        var stored = await new ProviderBankAccountRepository(verifyContext).GetByProviderIdAsync(provider.Id);
        stored.Should().NotBeNull();
        stored!.Id.Should().Be(firstId);
        stored.AccountNumber.Should().Be("222222222222");
    }

    [Fact]
    public async Task ApproveAsync_from_Pending_succeeds()
    {
        var provider = await SeedProviderAsync();
        var adminUserId = Guid.NewGuid();

        await using var context = _database.CreateContext();
        var service = CreateService(context);
        var submitted = await service.SubmitAsync(ValidRequest(provider.Id));

        var result = await service.ApproveAsync(submitted.Value.Id, adminUserId);

        result.IsSuccess.Should().BeTrue();
        result.Value.VerificationStatus.Should().Be(ProviderBankAccountVerificationStatus.Verified);
        result.Value.VerifiedBy.Should().Be(adminUserId);
        result.Value.VerifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ApproveAsync_when_not_Pending_fails()
    {
        var provider = await SeedProviderAsync();

        await using var context = _database.CreateContext();
        var service = CreateService(context);
        var submitted = await service.SubmitAsync(ValidRequest(provider.Id));
        (await service.ApproveAsync(submitted.Value.Id, Guid.NewGuid())).IsSuccess.Should().BeTrue();

        var result = await service.ApproveAsync(submitted.Value.Id, Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderBankAccount.AlreadyReviewed");
    }

    [Fact]
    public async Task RejectAsync_requires_a_reason()
    {
        var provider = await SeedProviderAsync();

        await using var context = _database.CreateContext();
        var service = CreateService(context);
        var submitted = await service.SubmitAsync(ValidRequest(provider.Id));

        // Mirrors ProviderKycApprovalService.RejectDocumentAsync: the empty-
        // reason guard lives on the domain method (ProviderBankAccount.Reject),
        // not the service - a caller reaching here with a blank reason has
        // already skipped FluentValidation's RejectProviderBankAccountRequestValidator
        // (the controller's own gate), so this documents that the domain
        // invariant still holds even if that gate is ever bypassed.
        var act = async () => await service.RejectAsync(submitted.Value.Id, Guid.NewGuid(), new RejectProviderBankAccountRequest(""));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RejectAsync_from_Pending_succeeds()
    {
        var provider = await SeedProviderAsync();
        var adminUserId = Guid.NewGuid();

        await using var context = _database.CreateContext();
        var service = CreateService(context);
        var submitted = await service.SubmitAsync(ValidRequest(provider.Id));

        var result = await service.RejectAsync(submitted.Value.Id, adminUserId, new RejectProviderBankAccountRequest("IFSC does not match the bank name"));

        result.IsSuccess.Should().BeTrue();
        result.Value.VerificationStatus.Should().Be(ProviderBankAccountVerificationStatus.Rejected);
        result.Value.VerifiedBy.Should().Be(adminUserId);
        result.Value.RejectionReason.Should().Be("IFSC does not match the bank name");
    }

    [Fact]
    public async Task ListPendingAsync_returns_only_pending_rows_oldest_first()
    {
        var older = await SeedProviderAsync("Older Provider");
        var newer = await SeedProviderAsync("Newer Provider");
        var reviewed = await SeedProviderAsync("Already Reviewed Provider");

        Guid olderId, newerId;
        await using (var context = _database.CreateContext())
        {
            var service = CreateService(context);
            var newerSubmission = await service.SubmitAsync(ValidRequest(newer.Id, "222200000000"));
            var olderSubmission = await service.SubmitAsync(ValidRequest(older.Id, "111100000000"));
            var reviewedSubmission = await service.SubmitAsync(ValidRequest(reviewed.Id, "333300000000"));
            newerId = newerSubmission.Value.Id;
            olderId = olderSubmission.Value.Id;

            await service.ApproveAsync(reviewedSubmission.Value.Id, Guid.NewGuid());
        }

        await using (var backdateContext = _database.CreateContext())
        {
            await backdateContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE provider_bank_account SET updated_at = {DateTime.UtcNow.AddDays(-2)} WHERE id = {olderId}");
            await backdateContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE provider_bank_account SET updated_at = {DateTime.UtcNow.AddDays(-1)} WHERE id = {newerId}");
        }

        await using var queryContext = _database.CreateContext();
        var queue = await CreateService(queryContext).ListPendingAsync();

        queue.Select(item => item.Id).Should().Equal(olderId, newerId);
        queue.Should().ContainSingle(item => item.Id == olderId && item.ProviderDisplayName == older.DisplayName);
        // Account number masked to last 4 digits in the queue row (not the full number).
        queue.Should().ContainSingle(item => item.Id == olderId && item.MaskedAccountNumber == "••••0000");
        queue.Select(item => item.ProviderId).Should().NotContain(reviewed.Id);
    }

    public void Dispose() => _database.Dispose();
}
