using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// A geography a provider covers (PROVIDER.md "provider_service_area: provider_id
/// -> city/zone/pincode coverage"). City is required (the coarsest level);
/// <see cref="ZoneId"/> and <see cref="PincodeId"/> optionally narrow
/// coverage to a zone or a specific pincode within that city, mirroring how
/// <c>Zone</c>/<c>Pincode</c> nest under <c>City</c> in the Geography module.
/// </summary>
public class ProviderServiceArea : Entity<Guid>
{
    public Guid ProviderId { get; private set; }
    public Guid CityId { get; private set; }
    public Guid? ZoneId { get; private set; }
    public Guid? PincodeId { get; private set; }
    public bool IsActive { get; private set; }

    protected ProviderServiceArea() { }

    public ProviderServiceArea(Guid id, Guid providerId, Guid cityId, Guid? zoneId = null, Guid? pincodeId = null) : base(id)
    {
        ProviderId = providerId;
        CityId = cityId;
        ZoneId = zoneId;
        PincodeId = pincodeId;
        IsActive = true;
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;
}
