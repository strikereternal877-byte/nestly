namespace Nestly.Application.Catalog;

/// <summary>
/// Reason codes <see cref="CatalogHealthIssueResponse.Reasons"/> can contain -
/// stable strings the admin-web catalog health screen matches on to render
/// badges, so they are not meant to be free-form/localised text.
/// </summary>
public static class CatalogHealthReason
{
    /// <summary>Zero <see cref="Nestly.Domain.ServiceCityPrice"/> rows are currently effective for this service (SRS 12.8.1 city pricing left unconfigured).</summary>
    public const string NoPrice = "NoPrice";

    /// <summary><see cref="Nestly.Domain.Service.CoverImageUrl"/> is null/empty.</summary>
    public const string NoImage = "NoImage";

    /// <summary>No active <see cref="Nestly.Domain.ServicePincodeMapping"/> anywhere - same condition as <see cref="Nestly.Application.Serviceability.UnmappedActiveServiceResponse"/>.</summary>
    public const string NoMapping = "NoMapping";

    /// <summary>No <see cref="Nestly.Domain.BookingItem"/> has ever referenced this service.</summary>
    public const string NeverBooked = "NeverBooked";
}

/// <summary>
/// docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Catalog
/// health": an active service failing at least one pre-publish completeness
/// check. Warning/audit only, matching <see cref="Nestly.Application.Serviceability.UnmappedActiveServiceResponse"/>'s
/// non-destructive approach - see <see cref="Nestly.Application.Catalog.IServiceManagementService.ListHealthIssuesAsync"/>'s
/// doc comment for why this never blocks creating or activating a service.
/// Only services with at least one failing check are returned - a service
/// passing every check is simply absent from the list, rather than every
/// active service being returned with an empty <see cref="Reasons"/>. This
/// keeps the admin table itself the signal (any row = something to fix) and
/// matches how the coverage-gap-map lists already work.
/// </summary>
public sealed record CatalogHealthIssueResponse(
    Guid ServiceId,
    string ServiceName,
    string ServiceSlug,
    Guid CategoryId,
    string CategoryName,
    IReadOnlyList<string> Reasons);
