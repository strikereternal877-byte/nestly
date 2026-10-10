namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "Msg91" configuration section - the real
/// SMS vendor for OTP and account notifications (SRS 30.2), chosen for its
/// India-focused pricing and DLT compliance support. Same optional-with-
/// fallback shape as <see cref="SupabaseStorageOptions"/>: absent credentials
/// mean SMS keeps simulating rather than failing the process at startup.
/// </summary>
/// <remarks>
/// <see cref="AuthKey"/> is a secret and must come from an environment
/// variable (<c>Msg91__AuthKey</c>) or secret store, never a committed
/// appsettings.json - see DEVOPS.md CONFIGURATION AND SECRETS.
///
/// <see cref="SenderId"/> and the message text actually sent must both match
/// a DLT (Distributed Ledger Technology) template registered with MSG91 and
/// approved by TRAI before delivery to Indian numbers works at all - this is
/// an Indian telecom regulation that applies to every SMS vendor, not an
/// MSG91-specific requirement, and is a business/compliance step (MSG91's own
/// dashboard walks through it) rather than something this configuration
/// section can express.
/// </remarks>
public class Msg91Options
{
    public const string SectionName = "Msg91";

    /// <summary>MSG91 API auth key, from the MSG91 dashboard. Secret - see remarks.</summary>
    public string? AuthKey { get; set; }

    /// <summary>
    /// The DLT-registered sender ID messages are sent from (6 characters,
    /// e.g. "GLAVYX") - not a secret, but only valid once DLT-approved for
    /// this account.
    /// </summary>
    public string? SenderId { get; set; }

    /// <summary>
    /// MSG91's transactional SMS route. "4" is MSG91's documented
    /// transactional/OTP route (as opposed to "1", promotional) - the correct
    /// choice for OTP and account notifications, which is all this
    /// integration sends. Exposed as configuration rather than hardcoded only
    /// so a route change from MSG91 never needs a code deploy.
    /// </summary>
    public string Route { get; set; } = "4";

    /// <summary>
    /// Kill switch. Default true, same convention as
    /// <see cref="SupabaseStorageOptions.Enabled"/>: forces the sandbox
    /// fallback even when credentials are present, without deleting them
    /// from the secret store.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True when SMS should go through MSG91: enabled and every credential is present. Mirrors <see cref="SupabaseStorageOptions.IsConfigured"/>.</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(AuthKey)
        && !string.IsNullOrWhiteSpace(SenderId);
}
