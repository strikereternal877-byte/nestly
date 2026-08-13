namespace Nestly.Application.Storage;

/// <summary>
/// Resolves an <see cref="IFileStorageService.SaveAsync"/> result to an
/// absolute URL. Shared by every controller that calls it (admin-api's
/// CmsMediaController, provider-api's JobsController) so both agree on the
/// same rule rather than each guessing independently.
/// </summary>
public static class FileReferenceUrl
{
    public static string ToAbsolute(string reference, string requestScheme, string requestHost) =>
        reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? reference
            : $"{requestScheme}://{requestHost}{reference}";
}
