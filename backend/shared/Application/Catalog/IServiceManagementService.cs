using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Catalog;

/// <summary>
/// Admin CRUD over services/packages (SRS 12.6): the full field set, service
/// option flags (12.6.3), gallery media and activation - every mutation is
/// recorded to the audit trail (<see cref="Nestly.Application.Abstractions.Auditing.IAuditLogWriter"/>).
/// </summary>
public interface IServiceManagementService
{
    /// <summary>All services, optionally filtered to one category, for the admin list screen.</summary>
    Task<IReadOnlyList<ServiceAdminResponse>> ListAsync(Guid? categoryId);

    Task<Result<ServiceAdminResponse>> GetByIdAsync(Guid id);
    Task<Result<ServiceAdminResponse>> CreateAsync(ServiceCreateRequest request);
    Task<Result<ServiceAdminResponse>> UpdateAsync(Guid id, ServiceUpdateRequest request);
    Task<Result> SetActiveAsync(Guid id, bool isActive);
    Task<Result> SetFeaturedAsync(Guid id, bool isFeatured);

    /// <summary>Gallery images attached to a service (SRS 12.6.2).</summary>
    Task<Result<IReadOnlyList<ServiceMediaResponse>>> ListMediaAsync(Guid serviceId);

    Task<Result<ServiceMediaResponse>> AddMediaAsync(Guid serviceId, ServiceMediaCreateRequest request);
    Task<Result> RemoveMediaAsync(Guid serviceId, Guid mediaId);

    /// <summary>
    /// docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Catalog
    /// health": flags every active service missing a currently-effective
    /// city price, a cover image, an active serviceability mapping, or that
    /// has never been booked. Reuses
    /// <see cref="Nestly.Application.Serviceability.IServiceabilityMappingManagementService.ListUnmappedActiveServicesAsync"/>
    /// for the mapping check rather than recomputing it. Non-destructive by
    /// design, same as that method's own doc comment: a warning/audit list
    /// for admins to work through, never a block on creating or activating a
    /// service - an incomplete service is a normal, valid state while its
    /// catalog entry is still being filled in.
    /// </summary>
    Task<IReadOnlyList<CatalogHealthIssueResponse>> ListHealthIssuesAsync();
}
