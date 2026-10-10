using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Corrects a specific, well-understood EF Core change-tracking gap: an
/// entity newly appended to an aggregate's owned collection after the
/// aggregate was already loaded and tracked by this context - e.g. a fresh
/// <see cref="BookingStatusHistory"/> row from <c>Booking.TransitionTo()</c>,
/// or a fresh <see cref="PaymentAttempt"/> from <c>PaymentTransaction.StartAttempt()</c>.
///
/// Because this domain assigns primary keys client-side (<c>Guid.NewGuid()</c>)
/// rather than relying on the database to generate them, such a row already
/// has a non-default key the moment it's constructed. EF's automatic change
/// detection discovers it only via graph fixup from an already-tracked
/// (not Added) parent, has no "loaded from the database" baseline to compare
/// that key against, and defaults to treating it as an existing row -
/// Modified - rather than a new one - Added. Left uncorrected, SaveChanges
/// emits an UPDATE for a row that was never inserted, and the database
/// reports "0 rows affected" as a spurious DbUpdateConcurrencyException.
///
/// This has to run centrally, on every SaveChanges regardless of which
/// repository/aggregate triggers it, rather than inside one repository's own
/// Update method: a single DbContext instance flushes ALL of its pending
/// changes together (e.g. the payment webhook handler updates a
/// PaymentTransaction and a Booking in the same SaveChanges), so whichever
/// repository happens to save first would otherwise flush the other
/// aggregate's not-yet-corrected new child rows too. It settles the question
/// the only reliable way available: checking the database for which of the
/// currently-Modified rows of these known owned-child types actually exist yet.
///
/// <see cref="SupportTicketComment"/> (task 86) joined this list for the
/// same reason: <c>SupportTicket.AddComment</c> appends a fresh, client-keyed
/// comment to an aggregate that was loaded (and is therefore already
/// tracked, not Added) by <c>SupportTicketService.AddCommentAsync</c>.
/// </summary>
public sealed class NewOwnedChildEntityInterceptor : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is NestlyDbContext context)
        {
            await ReconcileBookingStatusHistoryAsync(context, cancellationToken);
            await ReconcilePaymentAttemptsAsync(context, cancellationToken);
            await ReconcileSupportTicketCommentsAsync(context, cancellationToken);
            await ReconcileProviderStatusHistoryAsync(context, cancellationToken);
            await ReconcileProviderSupportTicketCommentsAsync(context, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static async Task ReconcileBookingStatusHistoryAsync(NestlyDbContext context, CancellationToken cancellationToken)
    {
        var modifiedEntries = context.ChangeTracker.Entries<BookingStatusHistory>()
            .Where(e => e.State == EntityState.Modified)
            .ToList();

        if (modifiedEntries.Count == 0)
        {
            return;
        }

        var candidateIds = modifiedEntries.Select(e => e.Entity.Id).ToList();
        var persistedIds = new HashSet<Guid>(
            await context.BookingStatusHistories.AsNoTracking()
                .Where(h => candidateIds.Contains(h.Id))
                .Select(h => h.Id)
                .ToListAsync(cancellationToken));

        foreach (var entry in modifiedEntries)
        {
            if (!persistedIds.Contains(entry.Entity.Id))
            {
                entry.State = EntityState.Added;
            }
        }
    }

    private static async Task ReconcilePaymentAttemptsAsync(NestlyDbContext context, CancellationToken cancellationToken)
    {
        var modifiedEntries = context.ChangeTracker.Entries<PaymentAttempt>()
            .Where(e => e.State == EntityState.Modified)
            .ToList();

        if (modifiedEntries.Count == 0)
        {
            return;
        }

        var candidateIds = modifiedEntries.Select(e => e.Entity.Id).ToList();
        var persistedIds = new HashSet<Guid>(
            await context.PaymentAttempts.AsNoTracking()
                .Where(a => candidateIds.Contains(a.Id))
                .Select(a => a.Id)
                .ToListAsync(cancellationToken));

        foreach (var entry in modifiedEntries)
        {
            if (!persistedIds.Contains(entry.Entity.Id))
            {
                entry.State = EntityState.Added;
            }
        }
    }

    private static async Task ReconcileSupportTicketCommentsAsync(NestlyDbContext context, CancellationToken cancellationToken)
    {
        var modifiedEntries = context.ChangeTracker.Entries<SupportTicketComment>()
            .Where(e => e.State == EntityState.Modified)
            .ToList();

        if (modifiedEntries.Count == 0)
        {
            return;
        }

        var candidateIds = modifiedEntries.Select(e => e.Entity.Id).ToList();
        var persistedIds = new HashSet<Guid>(
            await context.SupportTicketComments.AsNoTracking()
                .Where(c => candidateIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken));

        foreach (var entry in modifiedEntries)
        {
            if (!persistedIds.Contains(entry.Entity.Id))
            {
                entry.State = EntityState.Added;
            }
        }
    }

    private static async Task ReconcileProviderStatusHistoryAsync(NestlyDbContext context, CancellationToken cancellationToken)
    {
        var modifiedEntries = context.ChangeTracker.Entries<ProviderStatusHistory>()
            .Where(e => e.State == EntityState.Modified)
            .ToList();

        if (modifiedEntries.Count == 0)
        {
            return;
        }

        var candidateIds = modifiedEntries.Select(e => e.Entity.Id).ToList();
        var persistedIds = new HashSet<Guid>(
            await context.ProviderStatusHistories.AsNoTracking()
                .Where(h => candidateIds.Contains(h.Id))
                .Select(h => h.Id)
                .ToListAsync(cancellationToken));

        foreach (var entry in modifiedEntries)
        {
            if (!persistedIds.Contains(entry.Entity.Id))
            {
                entry.State = EntityState.Added;
            }
        }
    }

    private static async Task ReconcileProviderSupportTicketCommentsAsync(NestlyDbContext context, CancellationToken cancellationToken)
    {
        var modifiedEntries = context.ChangeTracker.Entries<ProviderSupportTicketComment>()
            .Where(e => e.State == EntityState.Modified)
            .ToList();

        if (modifiedEntries.Count == 0)
        {
            return;
        }

        var candidateIds = modifiedEntries.Select(e => e.Entity.Id).ToList();
        var persistedIds = new HashSet<Guid>(
            await context.ProviderSupportTicketComments.AsNoTracking()
                .Where(c => candidateIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken));

        foreach (var entry in modifiedEntries)
        {
            if (!persistedIds.Contains(entry.Entity.Id))
            {
                entry.State = EntityState.Added;
            }
        }
    }
}
