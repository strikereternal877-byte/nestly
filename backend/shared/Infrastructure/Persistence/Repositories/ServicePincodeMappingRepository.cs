using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Application.Serviceability;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ServicePincodeMappingRepository : IServicePincodeMappingRepository
{
    private readonly NestlyDbContext _context;

    public ServicePincodeMappingRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ServicePincodeMapping entity)
    {
        await _context.Set<ServicePincodeMapping>().AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(ServicePincodeMapping entity)
    {
        _context.Set<ServicePincodeMapping>().Update(entity);
        await _context.SaveChangesAsync();
    }

    public Task<ServicePincodeMapping?> GetByIdAsync(Guid id) =>
        _context.Set<ServicePincodeMapping>().FirstOrDefaultAsync(m => m.Id == id);

    public Task<bool> ExistsAsync(Guid id) =>
        _context.Set<ServicePincodeMapping>().AnyAsync(m => m.Id == id);

    public Task<ServicePincodeMapping?> FindAsync(Guid serviceId, Guid pincodeId) =>
        _context.Set<ServicePincodeMapping>()
            .FirstOrDefaultAsync(m => m.ServiceId == serviceId && m.PincodeId == pincodeId);

    public async Task<IReadOnlyList<ServicePincodeMappingResponse>> ListAsync(Guid? serviceId, Guid? pincodeId) =>
        await (
            from mapping in _context.Set<ServicePincodeMapping>()
            join service in _context.Set<Service>() on mapping.ServiceId equals service.Id
            join pincode in _context.Set<Pincode>() on mapping.PincodeId equals pincode.Id
            where (serviceId == null || mapping.ServiceId == serviceId) &&
                  (pincodeId == null || mapping.PincodeId == pincodeId)
            orderby pincode.Code, service.Name
            select new ServicePincodeMappingResponse(
                mapping.Id, service.Id, service.Name, pincode.Id, pincode.Code, mapping.IsActive)
        ).ToListAsync();

    public async Task<IReadOnlyList<UnmappedActiveServiceResponse>> ListUnmappedActiveServicesAsync()
    {
        var activelyMappedServiceIds = _context.Set<ServicePincodeMapping>()
            .Where(m => m.IsActive)
            .Select(m => m.ServiceId);

        return await (
            from service in _context.Set<Service>()
            join category in _context.Set<Category>() on service.CategoryId equals category.Id
            where service.IsActive && !activelyMappedServiceIds.Contains(service.Id)
            orderby service.Name
            select new UnmappedActiveServiceResponse(service.Id, service.Name, service.Slug, category.Id, category.Name)
        ).ToListAsync();
    }

    public async Task<IReadOnlyList<ServiceabilityCoverageGapResponse>> ListPincodesWithProviderCoverageButNoServiceMappingAsync() =>
        await CoverageGapQuery(providerId: null).ToListAsync();

    public async Task<IReadOnlyList<ServiceabilityCoverageGapResponse>> ListCoverablePairsForProviderAsync(Guid providerId) =>
        await CoverageGapQuery(providerId).ToListAsync();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ServiceabilityCoverageGapResponse>> ListMappedPairsCoveredByProviderAsync(Guid providerId) =>
        await (
            from service in _context.Set<Service>()
            where service.IsActive
            from pincode in _context.Set<Pincode>()
            where pincode.IsActive
            where _context.Set<ServicePincodeMapping>().Any(m =>
                m.IsActive && m.ServiceId == service.Id && m.PincodeId == pincode.Id)
            // Deliberately no Provider.Status check here - see the interface
            // doc comment. The caller takes this snapshot right before the
            // rows it reads (this provider's own skill/area rows, or their
            // Status) change, so the provider's current status is not part
            // of the "was this provider propping the mapping up" question.
            where _context.Set<ProviderSkillMapping>().Any(s =>
                s.ProviderId == providerId && s.IsActive && s.CategoryId == service.CategoryId &&
                (s.ServiceId == null || s.ServiceId == service.Id))
            where _context.Set<ProviderServiceArea>().Any(a =>
                a.ProviderId == providerId && a.IsActive && a.CityId == pincode.CityId &&
                (a.PincodeId == null || a.PincodeId == pincode.Id))
            orderby pincode.Code, service.Name
            select new ServiceabilityCoverageGapResponse(service.Id, service.Name, pincode.Id, pincode.Code)
        ).ToListAsync();

    /// <inheritdoc/>
    public async Task<bool> HasActiveProviderCoverageAsync(Guid serviceId, Guid pincodeId)
    {
        var service = await _context.Set<Service>()
            .Where(s => s.Id == serviceId)
            .Select(s => new { s.CategoryId })
            .FirstOrDefaultAsync();
        if (service is null)
        {
            return false;
        }

        var pincode = await _context.Set<Pincode>()
            .Where(p => p.Id == pincodeId)
            .Select(p => new { p.CityId })
            .FirstOrDefaultAsync();
        if (pincode is null)
        {
            return false;
        }

        // Same eligibility as CoverageGapQuery's inner provider-match Any(),
        // scoped to one known (service, pincode) pair rather than iterating
        // every combination - see the interface doc comment.
        return await _context.Set<Provider>().AnyAsync(p =>
            p.Status == ProviderStatus.Active &&
            _context.Set<ProviderSkillMapping>().Any(s =>
                s.ProviderId == p.Id && s.IsActive && s.CategoryId == service.CategoryId &&
                (s.ServiceId == null || s.ServiceId == serviceId)) &&
            _context.Set<ProviderServiceArea>().Any(a =>
                a.ProviderId == p.Id && a.IsActive && a.CityId == pincode.CityId &&
                (a.PincodeId == null || a.PincodeId == pincodeId)));
    }

    /// <summary>
    /// Shared by <see cref="ListPincodesWithProviderCoverageButNoServiceMappingAsync"/>
    /// (the admin-facing audit list, every gap) and
    /// <see cref="ListCoverablePairsForProviderAsync"/> (auto-enable's trigger
    /// query, scoped to the one provider that just gained a skill or area) -
    /// same skill + area eligibility <c>ProviderMatchingService.FindCandidatesAsync</c>
    /// uses to find booking candidates for a real booking - a category-level
    /// skill (ServiceId null) or city-wide area (PincodeId null) both count as
    /// covering, matching that service's own null-means-broader semantics.
    /// </summary>
    private IQueryable<ServiceabilityCoverageGapResponse> CoverageGapQuery(Guid? providerId)
    {
        var activelyMappedPairs = _context.Set<ServicePincodeMapping>()
            .Where(m => m.IsActive)
            .Select(m => new { m.ServiceId, m.PincodeId });

        return
            from service in _context.Set<Service>()
            where service.IsActive
            from pincode in _context.Set<Pincode>()
            where pincode.IsActive
            where _context.Set<Provider>().Any(p =>
                (providerId == null || p.Id == providerId) &&
                p.Status == ProviderStatus.Active &&
                _context.Set<ProviderSkillMapping>().Any(s =>
                    s.ProviderId == p.Id && s.IsActive && s.CategoryId == service.CategoryId &&
                    (s.ServiceId == null || s.ServiceId == service.Id)) &&
                _context.Set<ProviderServiceArea>().Any(a =>
                    a.ProviderId == p.Id && a.IsActive && a.CityId == pincode.CityId &&
                    (a.PincodeId == null || a.PincodeId == pincode.Id)))
            where !activelyMappedPairs.Any(m => m.ServiceId == service.Id && m.PincodeId == pincode.Id)
            orderby pincode.Code, service.Name
            select new ServiceabilityCoverageGapResponse(service.Id, service.Name, pincode.Id, pincode.Code);
    }
}
