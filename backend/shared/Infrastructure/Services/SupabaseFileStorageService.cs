using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application.Storage;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Real <see cref="IFileStorageService"/> backed by Supabase Storage
/// (docs/DEVOPS.md "CDN / media storage provider" OPEN DECISION), replacing
/// <see cref="LocalDiskFileStorageService"/>'s local disk - which is the
/// reason category/CMS images uploaded on one desktop never appeared on
/// another: the files lived only in that machine's git-ignored
/// <c>App_Data/uploads</c>, never in the (shared) database.
/// </summary>
/// <remarks>
/// Uses Supabase's own REST upload endpoint rather than the S3-compatible
/// one - a bearer token in a header, no AWS SigV4 signing - so no new SDK
/// dependency is needed, only <see cref="IHttpClientFactory"/>, already used
/// the same way by <see cref="GoogleMapsRouteEstimateProvider"/>.
/// </remarks>
public sealed class SupabaseFileStorageService : IFileStorageService
{
    /// <summary>
    /// Named <see cref="HttpClient"/> registration - see
    /// <see cref="GoogleMapsRouteEstimateProvider.HttpClientName"/> for why
    /// named rather than typed.
    /// </summary>
    public const string HttpClientName = "Supabase.Storage";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SupabaseStorageOptions _options;
    private readonly ILogger<SupabaseFileStorageService> _logger;

    public SupabaseFileStorageService(
        IHttpClientFactory httpClientFactory,
        IOptions<SupabaseStorageOptions> options,
        ILogger<SupabaseFileStorageService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> SaveAsync(Stream content, string fileNameHint, string contentType, CancellationToken cancellationToken = default)
    {
        // Same trust rule as LocalDiskFileStorageService: the hint's
        // extension is a display-name detail only, never the on-disk/object
        // name, and the caller has already validated contentType against an
        // allowlist before this runs.
        var extension = Path.GetExtension(fileNameHint);
        var objectPath = $"{Guid.NewGuid():N}{extension}";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"storage/v1/object/{_options.BucketName}/{objectPath}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServiceRoleKey);
        request.Headers.Add("apikey", _options.ServiceRoleKey);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Status code only, never the response body - Supabase error
            // bodies can echo request details that don't belong in logs.
            _logger.LogError("Supabase Storage upload failed with status {StatusCode}.", (int)response.StatusCode);
            throw new InvalidOperationException($"Supabase Storage upload failed with status {(int)response.StatusCode}.");
        }

        return $"{_options.ProjectUrl}/storage/v1/object/public/{_options.BucketName}/{objectPath}";
    }
}
