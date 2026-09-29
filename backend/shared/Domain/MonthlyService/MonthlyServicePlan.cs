using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain.MonthlyService;

/// <summary>
/// An admin-configured monthly service offering (docs/MONTHLY-SERVICE.md) -
/// e.g. "House help, 2 hours/visit, Jaipur, Rs 150/visit". Priced per visit
/// because billing is attendance-based: the month's invoice is billable
/// visits x <see cref="RatePerVisit"/>. Same catalog-row shape as
/// <see cref="AmcPlan"/>: owns only rules that are pure functions of its own
/// fields; name uniqueness lives in the admin service. Editing a plan never
/// changes an existing contract - every term is snapshotted onto
/// <see cref="MonthlyServiceContract"/> when it is requested.
/// </summary>
public class MonthlyServicePlan : Entity<Guid>
{
    public const int MaxIncludedTasks = 20;
    public const int MaxTaskLength = 100;
    public const decimal MaxCommissionPercent = 50m;

    private List<string> _includedTasks = [];

    public Guid ServiceId { get; private set; }

    public Guid CityId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public MonthlyServicePlanBasis Basis { get; private set; }

    /// <summary>Required for <see cref="MonthlyServicePlanBasis.Hourly"/>; null for task-based plans.</summary>
    public decimal? HoursPerVisit { get; private set; }

    /// <summary>Required (at least one) for <see cref="MonthlyServicePlanBasis.TaskBased"/>; optional guidance for hourly plans.</summary>
    public IReadOnlyList<string> IncludedTasks
    {
        get => _includedTasks;
        private set => _includedTasks = value.ToList();
    }

    public decimal RatePerVisit { get; private set; }

    /// <summary>Platform commission taken from each paid invoice, 0-50.</summary>
    public decimal CommissionPercent { get; private set; }

    /// <summary>How visits are scheduled - free weekdays (a maid), or N times per week / month (a car wash).</summary>
    public MonthlyServiceFrequency Frequency { get; private set; }

    /// <summary>The N in "N times a week/month"; null for <see cref="MonthlyServiceFrequency.Weekdays"/>.</summary>
    public int? TimesPerPeriod { get; private set; }

    public bool IsActive { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public Guid? UpdatedByAdminUserId { get; private set; }

    protected MonthlyServicePlan() { }

    public MonthlyServicePlan(
        Guid id,
        Guid serviceId,
        Guid cityId,
        string name,
        string? description,
        MonthlyServicePlanBasis basis,
        decimal? hoursPerVisit,
        IEnumerable<string>? includedTasks,
        decimal ratePerVisit,
        decimal commissionPercent,
        MonthlyServiceFrequency frequency = MonthlyServiceFrequency.Weekdays,
        int? timesPerPeriod = null)
        : base(id)
    {
        Apply(serviceId, cityId, name, description, basis, hoursPerVisit, includedTasks, ratePerVisit, commissionPercent, frequency, timesPerPeriod);
        IsActive = true;
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = CreatedAtUtc;
    }

    public void Update(
        Guid serviceId,
        Guid cityId,
        string name,
        string? description,
        MonthlyServicePlanBasis basis,
        decimal? hoursPerVisit,
        IEnumerable<string>? includedTasks,
        decimal ratePerVisit,
        decimal commissionPercent,
        Guid? updatedByAdminUserId,
        MonthlyServiceFrequency frequency = MonthlyServiceFrequency.Weekdays,
        int? timesPerPeriod = null)
    {
        Apply(serviceId, cityId, name, description, basis, hoursPerVisit, includedTasks, ratePerVisit, commissionPercent, frequency, timesPerPeriod);
        UpdatedAtUtc = DateTime.UtcNow;
        UpdatedByAdminUserId = updatedByAdminUserId;
    }

    public void Activate(Guid? updatedByAdminUserId)
    {
        IsActive = true;
        UpdatedAtUtc = DateTime.UtcNow;
        UpdatedByAdminUserId = updatedByAdminUserId;
    }

    /// <summary>Stops new requests on this plan; existing contracts keep running on their snapshotted terms.</summary>
    public void Deactivate(Guid? updatedByAdminUserId)
    {
        IsActive = false;
        UpdatedAtUtc = DateTime.UtcNow;
        UpdatedByAdminUserId = updatedByAdminUserId;
    }

    private void Apply(
        Guid serviceId,
        Guid cityId,
        string name,
        string? description,
        MonthlyServicePlanBasis basis,
        decimal? hoursPerVisit,
        IEnumerable<string>? includedTasks,
        decimal ratePerVisit,
        decimal commissionPercent,
        MonthlyServiceFrequency frequency,
        int? timesPerPeriod)
    {
        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException("A service is required.", nameof(serviceId));
        }

        if (cityId == Guid.Empty)
        {
            throw new ArgumentException("A city is required.", nameof(cityId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Plan name is required.", nameof(name));
        }

        var tasks = (includedTasks ?? [])
            .Select(t => t?.Trim() ?? string.Empty)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tasks.Count > MaxIncludedTasks)
        {
            throw new ArgumentOutOfRangeException(nameof(includedTasks), $"A plan can list at most {MaxIncludedTasks} tasks.");
        }

        if (tasks.Any(t => t.Length > MaxTaskLength))
        {
            throw new ArgumentOutOfRangeException(nameof(includedTasks), $"Each task must be at most {MaxTaskLength} characters.");
        }

        switch (basis)
        {
            case MonthlyServicePlanBasis.Hourly:
                if (hoursPerVisit is not > 0 || hoursPerVisit > 12)
                {
                    throw new ArgumentOutOfRangeException(nameof(hoursPerVisit), "An hourly plan needs between 0 and 12 hours per visit.");
                }

                break;
            case MonthlyServicePlanBasis.TaskBased:
                if (tasks.Count == 0)
                {
                    throw new ArgumentException("A task-based plan must list at least one task.", nameof(includedTasks));
                }

                hoursPerVisit = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(basis));
        }

        if (ratePerVisit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ratePerVisit), "Rate per visit must be positive.");
        }

        if (commissionPercent < 0 || commissionPercent > MaxCommissionPercent)
        {
            throw new ArgumentOutOfRangeException(nameof(commissionPercent), $"Commission must be between 0 and {MaxCommissionPercent}%.");
        }

        switch (frequency)
        {
            case MonthlyServiceFrequency.Weekdays:
                timesPerPeriod = null;
                break;
            case MonthlyServiceFrequency.TimesPerWeek:
                if (timesPerPeriod is not (>= 1 and <= 7))
                {
                    throw new ArgumentOutOfRangeException(nameof(timesPerPeriod), "Times per week must be between 1 and 7.");
                }

                break;
            case MonthlyServiceFrequency.TimesPerMonth:
                if (timesPerPeriod is not (>= 1 and <= MonthDays.MaxDay))
                {
                    throw new ArgumentOutOfRangeException(nameof(timesPerPeriod), $"Times per month must be between 1 and {MonthDays.MaxDay}.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(frequency));
        }

        ServiceId = serviceId;
        CityId = cityId;
        Frequency = frequency;
        TimesPerPeriod = timesPerPeriod;
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Basis = basis;
        HoursPerVisit = hoursPerVisit;
        _includedTasks = tasks;
        RatePerVisit = ratePerVisit;
        CommissionPercent = commissionPercent;
    }
}
