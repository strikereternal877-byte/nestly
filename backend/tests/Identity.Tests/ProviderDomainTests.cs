using FluentAssertions;
using Nestly.Domain;

namespace Nestly.Identity.Tests;

/// <summary>
/// Pure domain rules on <see cref="Provider"/> and <see cref="ProviderKycDocument"/>
/// (task 145a/145b, PROVIDER.md OPEN DECISIONS). No database involved - these
/// are invariants the entity itself enforces regardless of persistence.
/// </summary>
public class ProviderDomainTests
{
    [Fact]
    public void A_company_provider_cannot_be_created_in_this_release()
    {
        // OPEN DECISIONS #2: individuals only for v1.
        Action act = () => new Provider(
            Guid.NewGuid(), "Acme Services Pvt Ltd", "Acme Services", ProviderType.Company, "+919876543210");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_individual_provider_starts_pending_verification_with_registered_onboarding()
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");

        provider.Status.Should().Be(ProviderStatus.PendingVerification);
        provider.OnboardingStatus.Should().Be(ProviderOnboardingStatus.Registered);
    }

    [Fact]
    public void A_blank_legal_name_is_rejected()
    {
        Action act = () => new Provider(Guid.NewGuid(), "  ", "Ravi's Repairs", ProviderType.Individual, "+919876543210");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdateProfile_advances_onboarding_from_registered_to_profile_completed()
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");

        provider.UpdateProfile("Ravi Kumar", "Ravi's Home Repairs", "ravi@example.com");

        provider.OnboardingStatus.Should().Be(ProviderOnboardingStatus.ProfileCompleted);
    }

    [Fact]
    public void MarkKycSubmitted_is_idempotent_and_does_not_regress_a_later_onboarding_state()
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        provider.MarkKycSubmitted();
        provider.OnboardingStatus.Should().Be(ProviderOnboardingStatus.KycSubmitted);

        // A second submission (e.g. a re-upload) must not move the funnel backwards.
        provider.MarkKycSubmitted();
        provider.OnboardingStatus.Should().Be(ProviderOnboardingStatus.KycSubmitted);
    }

    [Fact]
    public void A_kyc_document_starts_pending_and_records_the_admin_who_approves_it()
    {
        var adminUserId = Guid.NewGuid();
        var document = new ProviderKycDocument(
            Guid.NewGuid(), Guid.NewGuid(), ProviderKycDocumentType.IdentityProof, "s3://kyc/doc.pdf");

        document.VerificationStatus.Should().Be(ProviderKycVerificationStatus.Pending);

        document.Approve(adminUserId);

        document.VerificationStatus.Should().Be(ProviderKycVerificationStatus.Approved);
        document.VerifiedBy.Should().Be(adminUserId);
        document.VerifiedAt.Should().NotBeNull();
    }
}
