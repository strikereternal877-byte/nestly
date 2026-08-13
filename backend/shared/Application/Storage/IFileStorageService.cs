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
}
