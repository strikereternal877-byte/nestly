namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "SupabaseStorage" configuration section -
/// the real, shared-across-desktops replacement for
/// <c>LocalDiskFileStorageService</c>, resolving docs/DEVOPS.md's "CDN /
/// media storage provider" OPEN DECISION. Same optional-with-fallback shape
/// as <see cref="GoogleMapsOptions"/>: absent credentials mean the local-disk
/// implementation keeps serving, no hard startup failure.
/// </summary>
/// <remarks>
/// <see cref="ServiceRoleKey"/> is a secret and must come from an environment
/// variable (<c>SupabaseStorage__ServiceRoleKey</c>) or secret store, never a
/// committed <c>appsettings.json</c> - see DEVOPS.md CONFIGURATION AND
/// SECRETS. It is the Supabase <b>service_role</b> key (bypasses row-level
/// security), so it must only ever be used server-side, exactly like this
/// class does - never sent to a browser.
/// </remarks>
public class SupabaseStorageOptions
{
    public const string SectionName = "SupabaseStorage";

    /// <summary>Project URL, e.g. "https://xxxxx.supabase.co". Not a secret.</summary>
    public string? ProjectUrl { get; set; }

    /// <summary>Supabase service_role API key. Secret - see remarks.</summary>
    public string? ServiceRoleKey { get; set; }

    /// <summary>
    /// Storage bucket uploads are written to. Must exist already (this
    /// service does not create buckets) and must be a <b>public</b> bucket -
    /// <see cref="SupabaseFileStorageService"/> returns the public object
    /// URL directly rather than minting signed URLs.
    /// </summary>
    public string BucketName { get; set; } = "uploads";

    /// <summary>
    /// Kill switch. Default true, same convention as
    /// <see cref="GoogleMapsOptions.Enabled"/>: forces the local-disk
    /// fallback even when credentials are present, without deleting them
    /// from the secret store.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Per-upload HTTP timeout.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// True when uploads should go to Supabase: the integration is switched
    /// on and both a project URL and a key exist. Mirrors
    /// <see cref="GoogleMapsOptions.IsConfigured"/>.
    /// </summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ProjectUrl) && !string.IsNullOrWhiteSpace(ServiceRoleKey);
}
