using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ServiceCityPriceRepository : IServiceCityPriceRepository
{
    private readonly NestlyDbContext _context;

    public ServiceCityPriceRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ServiceCityPrice entity)
    {
        await _context.Set<ServiceCityPrice>().AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(ServiceCityPrice entity)
    {
        _context.Set<ServiceCityPrice>().Update(entity);
        await _context.SaveChangesAsync();
    }

    public Task<ServiceCityPrice?> GetByIdAsync(Guid id) =>
        _context.Set<ServiceCityPrice>().FirstOrDefaultAsync(p => p.Id == id);

    public Task<bool> ExistsAsync(Guid id) =>
        _context.Set<ServiceCityPrice>().AnyAsync(p => p.Id == id);

    public Task<ServiceCityPrice?> GetForServiceAndCityAsync(Guid serviceId, Guid cityId) =>
        _context.Set<ServiceCityPrice>().FirstOrDefaultAsync(p => p.ServiceId == serviceId && p.CityId == cityId);

    public async Task<IReadOnlyList<ServiceCityPrice>> ListAsync(Guid? serviceId, Guid? cityId) =>
        await _context.Set<ServiceCityPrice>()
            .Where(p => serviceId == null || p.ServiceId == serviceId)
            .Where(p => cityId == null || p.CityId == cityId)
            .OrderByDescending(p => p.EffectiveStartDate)
            .ToListAsync();

    public async Task<IReadOnlyList<Guid>> ListServiceIdsWithActivePriceAsync()
    {
        // Inlines ServiceCityPrice.IsEffectiveOn's condition rather than
        // calling it - an instance method call doesn't translate to SQL.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await _context.Set<ServiceCityPrice>()
            .Where(p => p.EffectiveStartDate <= today && (p.EffectiveEndDate == null || p.EffectiveEndDate >= today))
            .Select(p => p.ServiceId)
            .Distinct()
            .ToListAsync();
    }
}
