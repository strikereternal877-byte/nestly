using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// A category (and optionally a specific service within it) a provider is
/// qualified/willing to work in (PROVIDER.md "provider_skill_mapping: provider_id
/// -> category/service they're qualified for"). <see cref="ServiceId"/> is
/// optional so a provider can declare capability at the broader category level
/// without listing every service in it; when present it narrows the mapping
/// to that one service. Shape mirrors <c>CategoryCityMapping</c>.
/// </summary>
public class ProviderSkillMapping : Entity<Guid>
{
    public Guid ProviderId { get; private set; }
    public Guid CategoryId { get; private set; }
    public Guid? ServiceId { get; private set; }
    public bool IsActive { get; private set; }

    protected ProviderSkillMapping() { }

    public ProviderSkillMapping(Guid id, Guid providerId, Guid categoryId, Guid? serviceId = null) : base(id)
    {
        ProviderId = providerId;
        CategoryId = categoryId;
        ServiceId = serviceId;
        IsActive = true;
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;
}
