namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "ProviderReferral" configuration section,
/// mirrors <see cref="ReferralOptions"/>. Only the share-link base URL lives
/// here rather than in a DB row - same "not-yet-adminable, options-file for
/// now" reasoning as <see cref="ReferralOptions"/>.
/// </summary>
public class ProviderReferralOptions
{
    public const string SectionName = "ProviderReferral";

    /// <summary>Provider-web registration URL a referral code is appended to as a query parameter. Defaults to the live provider-web deployment; override via "ProviderReferral:ShareLinkBaseUrl" once a custom domain exists.</summary>
    public string ShareLinkBaseUrl { get; set; } = "https://glavyx-provider-web.vercel.app/register?ref=";
}
