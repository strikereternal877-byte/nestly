using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nestly.Application.Storage;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IFileStorageService"/>
public class LocalDiskFileStorageService : IFileStorageService
{
    private readonly string _uploadsDirectory;
    private readonly string _requestPath;

    public LocalDiskFileStorageService(IHostEnvironment environment, IOptions<FileStorageOptions> options)
    {
        _uploadsDirectory = Path.Combine(environment.ContentRootPath, options.Value.UploadsPath);
        _requestPath = options.Value.RequestPath;
        Directory.CreateDirectory(_uploadsDirectory);
    }

    public async Task<string> SaveAsync(Stream content, string fileNameHint, string contentType, CancellationToken cancellationToken = default)
    {
        // The hint's own extension is trusted only as a display-name detail
        // never as the on-disk name; the caller (JobsController) has already
        // validated contentType against an allowlist before this runs.
        var extension = Path.GetExtension(fileNameHint);
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(_uploadsDirectory, fileName);

        await using var destination = File.Create(fullPath);
        await content.CopyToAsync(destination, cancellationToken);

        return $"{_requestPath}/{fileName}";
    }

    public Task DeleteAsync(string fileReference, CancellationToken cancellationToken = default)
    {
        // SaveAsync itself returns a bare "{_requestPath}/{fileName}" reference,
        // but callers (e.g. ProviderProfileService.SubmitPhoto via
        // FileReferenceUrl.ToAbsolute) routinely turn that into an absolute
        // "http://host/uploads/xxx.jpg" URL before persisting it - the same
        // shape a real Provider.PhotoUrl/ProviderKycDocument.FileRef has in
        // this environment. Resolving through Uri first, rather than a plain
        // StartsWith on the raw string, is what makes both shapes match the
        // same file instead of only the relative one SaveAsync itself
        // returns (which nothing actually persists as-is).
        var path = Uri.TryCreate(fileReference, UriKind.Absolute, out var absoluteUri) ? absoluteUri.AbsolutePath : fileReference;

        // Inverse of SaveAsync's return value - see SupabaseFileStorageService.DeleteAsync
        // for why an unrecognized shape (a Supabase public URL) is a silent no-op here.
        var prefix = $"{_requestPath}/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var fileName = path[prefix.Length..];
        var fullPath = Path.Combine(_uploadsDirectory, fileName);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }
}
