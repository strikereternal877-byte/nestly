using Nestly.Domain;

namespace Nestly.Application;

public interface IServiceCityPriceRepository : IRepository<ServiceCityPrice>
{
    Task<ServiceCityPrice?> GetForServiceAndCityAsync(Guid serviceId, Guid cityId);

    /// <summary>City price overrides, optionally filtered by service and/or city, for the admin pricing screens (SRS 12.8.1).</summary>
    Task<IReadOnlyList<ServiceCityPrice>> ListAsync(Guid? serviceId, Guid? cityId);

    /// <summary>
    /// Distinct <see cref="ServiceCityPrice.ServiceId"/>s with at least one
    /// row effective today (<see cref="ServiceCityPrice.IsEffectiveOn"/>) -
    /// docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Catalog
    /// health"'s "missing price" check. A service absent from this set has
    /// literally zero active city-price rows, even though its own base
    /// <see cref="Nestly.Domain.Service.Price"/> is always positive by
    /// construction - the catalog health check flags city pricing being
    /// unconfigured, not the base price field.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListServiceIdsWithActivePriceAsync();
}
