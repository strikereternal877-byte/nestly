using Microsoft.EntityFrameworkCore;
using Nestly.Application.Meals;
using Nestly.Domain.Meals;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IMealPreferenceService"/>
public sealed class MealPreferenceService : IMealPreferenceService
{
    private readonly NestlyDbContext _context;
    private readonly TimeProvider _timeProvider;

    public MealPreferenceService(NestlyDbContext context, TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<MealPreferencesDto?> GetAsync(Guid customerId)
    {
        var saved = await _context.Set<CustomerMealPreference>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.CustomerId == customerId);
        return saved?.Preferences.ToDto();
    }

    public async Task RememberAsync(Guid customerId, MealPreferences preferences)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var saved = _context.Set<CustomerMealPreference>().Local.FirstOrDefault(p => p.CustomerId == customerId)
            ?? await _context.Set<CustomerMealPreference>().FirstOrDefaultAsync(p => p.CustomerId == customerId);
        if (saved is null)
        {
            _context.Set<CustomerMealPreference>().Add(new CustomerMealPreference(Guid.NewGuid(), customerId, preferences, now));
        }
        else
        {
            saved.Update(preferences, now);
        }
    }
}
