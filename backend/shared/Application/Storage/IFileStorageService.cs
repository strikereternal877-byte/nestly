namespace Nestly.Application.Storage;

/// <summary>
/// Binary file storage - <c>LocalDiskFileStorageService</c> (dev/local only:
/// files land in the git-ignored <c>App_Data/uploads</c>, so they never
/// leave the machine they were uploaded on) or <c>SupabaseFileStorageService</c>
/// (docs/DEVOPS.md OPEN DECISIONS' CDN/media storage provider, resolved),
/// chosen at startup by <c>FileStorageRegistration</c> depending on whether
/// Supabase credentials are configured.
/// </summary>
public interface IFileStorageService
{
    /// <summary>
    /// Persists <paramref name="content"/> under a server-generated name (the
    /// caller's <paramref name="fileNameHint"/> is never trusted as a path -
    /// only its extension, if any, is considered) and returns a reference the
    /// content is servable back from - either a path relative to this API's
    /// own origin (e.g. "/uploads/&lt;guid&gt;.jpg", local disk) or an
    /// already-absolute URL (Supabase's public object URL). Callers must
    /// resolve the result through <see cref="FileReferenceUrl.ToAbsolute"/>
    /// rather than assuming either shape.
    /// </summary>
    Task<string> SaveAsync(Stream content, string fileNameHint, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort removal of a previously <see cref="SaveAsync"/>-returned
    /// reference. Exists for right-to-erasure flows (see
    /// <c>Provider.SoftDelete</c>/<c>ProviderManagementService.DeleteAsync</c>)
    /// so an already-anonymized DB row doesn't leave its uploaded file (a KYC
    /// document, a profile photo) behind in storage. Implementations must
    /// treat "already gone" (404, missing local file) as success, and must
    /// silently no-op - never throw - for a reference that isn't theirs to
    /// delete (e.g. one saved by the other implementation before a storage
    /// migration), since the caller has no way to route by origin.
    /// </summary>
    Task DeleteAsync(string fileReference, CancellationToken cancellationToken = default);
}
