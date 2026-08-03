namespace Nestly.Application.ProviderIdentity;

/// <summary>Step 1 of provider registration: request an OTP be sent to a mobile number.</summary>
public record RequestProviderRegistrationOtpRequest(string Mobile);

/// <summary>
/// Step 2 of provider registration: the OTP proves ownership of the mobile
/// number (mirrors <c>RegisterCustomerRequest</c>). Unlike customer
/// registration, there is no optional email+password mode - PROVIDER.md's API
/// surface lists only OTP-based auth for providers.
/// </summary>
public record RegisterProviderRequest(
    string Mobile,
    string OtpCode,
    string LegalName,
    string DisplayName,
    string? Email,
    bool ConsentAccepted);

/// <summary>Never includes anything auth-sensitive.</summary>
public record ProviderSummaryResponse(
    Guid Id,
    string LegalName,
    string DisplayName,
    string Phone,
    string? Email,
    string Status,
    string OnboardingStatus);
