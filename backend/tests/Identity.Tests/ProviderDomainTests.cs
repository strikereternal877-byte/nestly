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
    public void UpdateLocation_sets_both_coordinates_together()
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");

        provider.UpdateLocation(12.9716m, 77.5946m);

        provider.Latitude.Should().Be(12.9716m);
        provider.Longitude.Should().Be(77.5946m);
    }

    [Fact]
    public void UpdateLocation_clears_a_previously_set_location_when_given_both_null()
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        provider.UpdateLocation(12.9716m, 77.5946m);

        provider.UpdateLocation(null, null);

        provider.Latitude.Should().BeNull();
        provider.Longitude.Should().BeNull();
    }

    [Theory]
    [InlineData(12.9716, null)]
    [InlineData(null, 77.5946)]
    public void UpdateLocation_rejects_only_one_coordinate_set(double? latitude, double? longitude)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");

        Action act = () => provider.UpdateLocation((decimal?)latitude, (decimal?)longitude);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_provider_who_has_never_shared_a_location_has_no_location_timestamp()
    {
        // Task 268: null here means "never located", which must stay
        // distinguishable from "located, but long ago".
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");

        provider.LocationUpdatedAtUtc.Should().BeNull();
    }

    [Fact]
    public void UpdateLocation_stamps_the_current_time_when_the_caller_did_not_observe_the_fix()
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        var before = DateTime.UtcNow;

        provider.UpdateLocation(12.9716m, 77.5946m);

        provider.LocationUpdatedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    public void UpdateLocation_keeps_the_observed_time_a_caller_supplies()
    {
        // Task 268: a ping delivered late (queued upload) must not be stamped
        // "now" - that is exactly what makes a stale position look fresh.
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        var observedAtUtc = DateTime.UtcNow.AddMinutes(-7);

        provider.UpdateLocation(12.9716m, 77.5946m, observedAtUtc);

        provider.LocationUpdatedAtUtc.Should().Be(observedAtUtc);
    }

    [Fact]
    public void UpdateLocation_clears_the_location_timestamp_along_with_the_coordinates()
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        provider.UpdateLocation(12.9716m, 77.5946m);

        provider.UpdateLocation(null, null);

        provider.LocationUpdatedAtUtc.Should().BeNull();
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

    /// <summary>
    /// Regression: the real "phone" column is varchar(20) (ProviderConfiguration),
    /// but SoftDelete's anonymized placeholder used to be
    /// "deleted-{32 hex chars}" - 40 characters, silently accepted by a test
    /// database that doesn't enforce VARCHAR length but rejected by real
    /// Postgres with 22001 ("value too long"), which an in-memory-only unit
    /// test could never have caught. Found by an end-to-end run of the admin
    /// Delete Provider endpoint against a real local Postgres.
    /// </summary>
    [Fact]
    public void SoftDelete_anonymized_phone_fits_the_varchar_20_column()
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");

        provider.SoftDelete("Right-to-erasure request.");

        provider.Phone.Length.Should().BeLessOrEqualTo(20);
        provider.Phone.Should().StartWith("deleted-");
    }
}
