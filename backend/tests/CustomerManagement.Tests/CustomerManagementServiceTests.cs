using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Application.CustomerRatings;
using Nestly.Application.Customers;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.CustomerManagement.Tests;

/// <summary>
/// Admin customer management (SRS 12.4, tasks 101a-101d), exercised through
/// <see cref="CustomerManagementService"/> and the real repositories over a
/// relational (SQLite) database - same rationale as
/// Identity.Tests/AdminLoginServiceTests: the search filters, the booking
/// count subquery, and the unique/lookup behaviour are all decided in SQL, so
/// stubbed repositories would not exercise the thing that can actually break.
/// </summary>
public class CustomerManagementServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private CustomerManagementService CreateService(NestlyDbContext context) =>
        new(
            new CustomerRepository(context),
            new CustomerAddressRepository(context),
            new BookingRepository(context),
            new WalletLedgerRepository(context),
            new WalletService(new WalletLedgerRepository(context), context),
            new CouponRedemptionRepository(context),
            new CouponRepository(context),
            new SupportTicketRepository(context),
            new CustomerNoteRepository(context),
            new CustomerRatingRepository(context),
            new CustomerSessionRepository(context));

    private static Customer NewCustomer(string name, string mobile, CustomerStatus status = CustomerStatus.Active, string? city = null, string? email = null) =>
        new(Guid.NewGuid(), mobile, name, status, email: email, city: city);

    [Fact]
    public async Task SearchAsync_FiltersByNameCaseInsensitively()
    {
        var alice = NewCustomer("Alice Johnson", "9000000001");
        var bob = NewCustomer("Bob Smith", "9000000002");

        await using (var context = _database.CreateContext())
        {
            context.Add(alice);
            context.Add(bob);
            await context.SaveChangesAsync();
        }

        await using var readContext = _database.CreateContext();
        var service = CreateService(readContext);

        var result = await service.SearchAsync(new CustomerSearchRequest(
            Name: "alice", Mobile: null, Email: null, City: null, Status: null,
            RegisteredFromUtc: null, RegisteredToUtc: null, MinBookingCount: null, MaxBookingCount: null));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Single().Id.Should().Be(alice.Id);
    }

    [Fact]
    public async Task SearchAsync_FiltersByStatus()
    {
        var active = NewCustomer("Active Customer", "9000000010", CustomerStatus.Active);
        var blocked = NewCustomer("Blocked Customer", "9000000011", CustomerStatus.Blocked);

        await using (var context = _database.CreateContext())
        {
            context.AddRange(active, blocked);
            await context.SaveChangesAsync();
        }

        await using var readContext = _database.CreateContext();
        var service = CreateService(readContext);

        var result = await service.SearchAsync(new CustomerSearchRequest(
            null, null, null, null, CustomerStatus.Blocked, null, null, null, null));

        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Single().Status.Should().Be(CustomerStatus.Blocked);
    }

    [Fact]
    public async Task SearchAsync_ComputesBookingCountAndFiltersByIt()
    {
        var withBooking = NewCustomer("Has Booking", "9000000020");
        var withoutBooking = NewCustomer("No Booking", "9000000021");

        await using (var context = _database.CreateContext())
        {
            context.AddRange(withBooking, withoutBooking);
            context.Add(CreateBooking(withBooking.Id));
            await context.SaveChangesAsync();
        }

        await using var readContext = _database.CreateContext();
        var service = CreateService(readContext);

        var all = await service.SearchAsync(new CustomerSearchRequest(null, null, null, null, null, null, null, null, null));
        all.Value.Items.Single(i => i.Id == withBooking.Id).BookingCount.Should().Be(1);
        all.Value.Items.Single(i => i.Id == withoutBooking.Id).BookingCount.Should().Be(0);

        var onlyWithBookings = await service.SearchAsync(new CustomerSearchRequest(
            null, null, null, null, null, null, null, MinBookingCount: 1, MaxBookingCount: null));
        onlyWithBookings.Value.TotalCount.Should().Be(1);
        onlyWithBookings.Value.Items.Single().Id.Should().Be(withBooking.Id);
    }

    [Fact]
    public async Task SearchAsync_PaginatesResults()
    {
        await using (var context = _database.CreateContext())
        {
            for (int i = 0; i < 5; i++)
            {
                context.Add(NewCustomer($"Customer {i}", $"90000003{i}"));
            }

            await context.SaveChangesAsync();
        }

        await using var readContext = _database.CreateContext();
        var service = CreateService(readContext);

        var page1 = await service.SearchAsync(new CustomerSearchRequest(null, null, null, null, null, null, null, null, null, Page: 1, PageSize: 2));
        var page2 = await service.SearchAsync(new CustomerSearchRequest(null, null, null, null, null, null, null, null, null, Page: 2, PageSize: 2));

        page1.Value.TotalCount.Should().Be(5);
        page1.Value.Items.Should().HaveCount(2);
        page2.Value.Items.Should().HaveCount(2);
        page1.Value.Items.Select(i => i.Id).Should().NotIntersectWith(page2.Value.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetDetailAsync_UnknownCustomer_ReturnsNotFound()
    {
        await using var context = _database.CreateContext();
        var service = CreateService(context);

        var result = await service.GetDetailAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.NotFound");
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsFullThreeSixtyView()
    {
        var customer = NewCustomer("Full View Customer", "9000000040");
        var address = new CustomerAddress(
            Guid.NewGuid(), customer.Id, "Home", "12 Main St", null, null, "560001", "Bengaluru", "Karnataka",
            12.9m, 77.5m, "Full View Customer", "9000000040", isDefault: true);
        var booking = CreateBooking(customer.Id);
        var walletEntry = new WalletLedgerEntry(
            Guid.NewGuid(), customer.Id, WalletEntryType.Credit, 100m, 100m,
            WalletSourceType.Refund, null, "Refund credit");
        var coupon = new Coupon(
            Guid.NewGuid(), "WELCOME10", "10% off", CouponDiscountType.Percentage, 10m, 50m, 0m,
            DateTime.UtcNow.AddDays(-10), DateTime.UtcNow.AddDays(10), null, null, null, CouponCustomerSegment.All);
        var redemption = new CouponRedemption(Guid.NewGuid(), coupon.Id, customer.Id, booking.Id, 20m);
        var ticket = new SupportTicket(Guid.NewGuid(), customer.Id, booking.Id, SupportTicketCategory.BookingIssue, "Late arrival", "The professional arrived late.");

        await using (var context = _database.CreateContext())
        {
            context.Add(customer);
            context.Add(address);
            context.Add(booking);
            context.Add(walletEntry);
            context.Add(coupon);
            context.Add(redemption);
            context.Add(ticket);
            await context.SaveChangesAsync();
        }

        await using var readContext = _database.CreateContext();
        var service = CreateService(readContext);
        await service.AddNoteAsync(customer.Id, Guid.NewGuid(), "First contact made.");

        await using var readContext2 = _database.CreateContext();
        var service2 = CreateService(readContext2);
        var result = await service2.GetDetailAsync(customer.Id);

        result.IsSuccess.Should().BeTrue();
        var detail = result.Value;
        detail.Addresses.Should().ContainSingle(a => a.Id == address.Id);
        detail.Bookings.Should().ContainSingle(b => b.Id == booking.Id);
        detail.WalletBalance.Should().Be(100m);
        detail.WalletEntries.Should().ContainSingle(w => w.Id == walletEntry.Id);
        detail.Coupons.Should().ContainSingle(c => c.CouponCode == "WELCOME10" && c.DiscountAmount == 20m);
        detail.SupportTickets.Should().ContainSingle(t => t.Id == ticket.Id);
        detail.Notes.Should().ContainSingle(n => n.Note == "First contact made.");
    }

    [Fact]
    public async Task BlockAsync_ActiveCustomer_BlocksAndRecordsNote()
    {
        var customer = NewCustomer("To Block", "9000000050");
        await using (var context = _database.CreateContext())
        {
            context.Add(customer);
            await context.SaveChangesAsync();
        }

        Guid adminUserId = Guid.NewGuid();
        await using var context1 = _database.CreateContext();
        var result = await CreateService(context1).BlockAsync(customer.Id, adminUserId, "Repeated fraudulent chargebacks.");

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(CustomerStatus.Blocked);
        result.Value.Notes.Should().ContainSingle(n => n.Note.Contains("Repeated fraudulent chargebacks."));

        await using var context2 = _database.CreateContext();
        var persisted = await context2.Set<Customer>().FindAsync(customer.Id);
        persisted!.Status.Should().Be(CustomerStatus.Blocked);
    }

    [Fact]
    public async Task BlockAsync_SoftDeletedCustomer_ReturnsBusinessError()
    {
        var customer = NewCustomer("Deleted Customer", "9000000051", CustomerStatus.SoftDeleted);
        await using (var context = _database.CreateContext())
        {
            context.Add(customer);
            await context.SaveChangesAsync();
        }

        await using var readContext = _database.CreateContext();
        var result = await CreateService(readContext).BlockAsync(customer.Id, Guid.NewGuid(), "Any reason");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.CannotBlockDeleted");
    }

    [Fact]
    public async Task UnblockAsync_NotCurrentlyBlocked_ReturnsBusinessError()
    {
        var customer = NewCustomer("Active Customer", "9000000060", CustomerStatus.Active);
        await using (var context = _database.CreateContext())
        {
            context.Add(customer);
            await context.SaveChangesAsync();
        }

        await using var readContext = _database.CreateContext();
        var result = await CreateService(readContext).UnblockAsync(customer.Id, Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.NotBlocked");
    }

    [Fact]
    public async Task UnblockAsync_BlockedCustomer_RestoresActiveAndRecordsNote()
    {
        var customer = NewCustomer("Blocked Customer", "9000000061", CustomerStatus.Blocked);
        await using (var context = _database.CreateContext())
        {
            context.Add(customer);
            await context.SaveChangesAsync();
        }

        await using var readContext = _database.CreateContext();
        var result = await CreateService(readContext).UnblockAsync(customer.Id, Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(CustomerStatus.Active);
        result.Value.Notes.Should().ContainSingle(n => n.Note.Contains("unblocked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteAsync_ActiveCustomer_AnonymizesAndRecordsNote()
    {
        var customer = NewCustomer("To Delete", "9000000052", email: "todelete@example.com");
        await using (var context = _database.CreateContext())
        {
            context.Add(customer);
            await context.SaveChangesAsync();
        }

        Guid adminUserId = Guid.NewGuid();
        await using var context1 = _database.CreateContext();
        var result = await CreateService(context1).DeleteAsync(customer.Id, adminUserId, "GDPR erasure request via support ticket.");

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(CustomerStatus.SoftDeleted);
        result.Value.Name.Should().Be("Deleted User");
        result.Value.Notes.Should().ContainSingle(n => n.Note.Contains("GDPR erasure request via support ticket."));

        await using var context2 = _database.CreateContext();
        var persisted = await context2.Set<Customer>().FindAsync(customer.Id);
        persisted!.Status.Should().Be(CustomerStatus.SoftDeleted);
        persisted.Email.Should().NotBe("todelete@example.com");

        // Regression: "mobile" is varchar(20) (CustomerConfiguration), but the
        // anonymized placeholder used to be "deleted-{32 hex chars}" - 40
        // characters. This test's SQLite-backed TestDatabase doesn't enforce
        // VARCHAR length, so it kept passing even though the same call threw
        // 22001 ("value too long") against a real Postgres column - found by
        // an end-to-end run of the equivalent admin Delete Provider endpoint,
        // which shares this exact bug pattern.
        persisted.Mobile.Length.Should().BeLessOrEqualTo(20);
    }

    [Fact]
    public async Task DeleteAsync_AlreadyDeletedCustomer_ReturnsBusinessError()
    {
        var customer = NewCustomer("Already Deleted", "9000000053", CustomerStatus.SoftDeleted);
        await using (var context = _database.CreateContext())
        {
            context.Add(customer);
            await context.SaveChangesAsync();
        }

        await using var readContext = _database.CreateContext();
        var result = await CreateService(readContext).DeleteAsync(customer.Id, Guid.NewGuid(), "Any reason");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.AlreadyDeleted");
    }

    [Fact]
    public async Task AddNoteAsync_UnknownCustomer_ReturnsNotFound()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).AddNoteAsync(Guid.NewGuid(), Guid.NewGuid(), "A note");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.NotFound");
    }

    [Fact]
    public async Task AddNoteAsync_ValidCustomer_PersistsNote()
    {
        var customer = NewCustomer("Noted Customer", "9000000070");
        await using (var context = _database.CreateContext())
        {
            context.Add(customer);
            await context.SaveChangesAsync();
        }

        Guid adminUserId = Guid.NewGuid();
        await using var context1 = _database.CreateContext();
        var result = await CreateService(context1).AddNoteAsync(customer.Id, adminUserId, "Called about a refund query.");

        result.IsSuccess.Should().BeTrue();
        result.Value.AuthorAdminUserId.Should().Be(adminUserId);

        await using var context2 = _database.CreateContext();
        var notes = await new CustomerNoteRepository(context2).ListByCustomerAsync(customer.Id);
        notes.Should().ContainSingle(n => n.Note == "Called about a refund query.");
    }

    [Fact]
    public async Task AdjustWalletAsync_Credit_AppendsLedgerEntryAndRecordsNote()
    {
        var customer = NewCustomer("Wallet Credit Customer", "9000000090");
        await using (var context = _database.CreateContext())
        {
            context.Add(customer);
            await context.SaveChangesAsync();
        }

        Guid adminUserId = Guid.NewGuid();
        await using var context1 = _database.CreateContext();
        var result = await CreateService(context1).AdjustWalletAsync(
            customer.Id, adminUserId, new AdjustCustomerWalletRequest(WalletEntryType.Credit, 150m, "Goodwill gesture for a delayed service."));

        result.IsSuccess.Should().BeTrue();
        result.Value.WalletBalance.Should().Be(150m);
        result.Value.WalletEntries.Should().ContainSingle(w =>
            w.EntryType == WalletEntryType.Credit && w.Amount == 150m && w.SourceType == WalletSourceType.ManualAdjustment);
        result.Value.Notes.Should().ContainSingle(n => n.Note.Contains("Goodwill gesture for a delayed service."));
    }

    [Fact]
    public async Task AdjustWalletAsync_DebitExceedingBalance_ReturnsBusinessErrorAndRecordsNoNote()
    {
        var customer = NewCustomer("Wallet Debit Customer", "9000000091");
        await using (var context = _database.CreateContext())
        {
            context.Add(customer);
            await context.SaveChangesAsync();
        }

        await using var readContext = _database.CreateContext();
        var result = await CreateService(readContext).AdjustWalletAsync(
            customer.Id, Guid.NewGuid(), new AdjustCustomerWalletRequest(WalletEntryType.Debit, 50m, "Correction"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Wallet.InsufficientBalance");

        await using var verifyContext = _database.CreateContext();
        var notes = await new CustomerNoteRepository(verifyContext).ListByCustomerAsync(customer.Id);
        notes.Should().BeEmpty("a failed debit must not leave a stray audit note behind");
    }

    [Fact]
    public async Task AdjustWalletAsync_UnknownCustomer_ReturnsNotFound()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).AdjustWalletAsync(
            Guid.NewGuid(), Guid.NewGuid(), new AdjustCustomerWalletRequest(WalletEntryType.Credit, 10m, "Any reason"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Customer.NotFound");
    }

    /// <summary>
    /// Customer Analytics dashboard (Admin Web new page): one customer per
    /// status bucket, a same-day registration, a booking to exercise the
    /// acquisition-vs-activation funnel, and a city to exercise the
    /// breakdown table - then asserts every aggregate in one pass, same
    /// "seed the whole cohort, assert every count" shape as
    /// Identity.Tests/ProviderManagementServiceTests's
    /// GetOnboardingOverviewAsync_counts_only_todays_cohort_per_stage.
    /// </summary>
    [Fact]
    public async Task GetAnalyticsAsync_ComputesCountsTrendAndCityBreakdown()
    {
        var activeBengaluru = NewCustomer("Active Bengaluru", "9000000080", CustomerStatus.Active, city: "Bengaluru");
        var activeMumbai = NewCustomer("Active Mumbai", "9000000081", CustomerStatus.Active, city: "Mumbai");
        var blocked = NewCustomer("Blocked Customer", "9000000082", CustomerStatus.Blocked, city: "Bengaluru");
        var unverified = NewCustomer("Unverified Customer", "9000000083", CustomerStatus.Unverified);
        var softDeleted = NewCustomer("Deleted Customer", "9000000084", CustomerStatus.SoftDeleted);
        var oldNoBooking = NewCustomer("Old No Booking", "9000000085", CustomerStatus.Active);

        await using (var context = _database.CreateContext())
        {
            context.AddRange(activeBengaluru, activeMumbai, blocked, unverified, softDeleted, oldNoBooking);
            context.Add(CreateBooking(activeBengaluru.Id));
            await context.SaveChangesAsync();
        }

        // oldNoBooking registered outside the 30-day trend window - must not
        // count toward NewInTrendWindow/NewLast7Days/RegistrationTrend, but
        // must still count toward TotalCustomers and CustomersWithZeroBookings.
        await BackdateCustomerCreatedAtAsync(oldNoBooking.Id, DateTime.UtcNow.AddDays(-45));

        await using var readContext = _database.CreateContext();
        var result = await CreateService(readContext).GetAnalyticsAsync(new CustomerAnalyticsRequest(TrendDays: 30));

        result.IsSuccess.Should().BeTrue();
        var analytics = result.Value;

        analytics.TrendDays.Should().Be(30);
        analytics.TotalCustomers.Should().Be(6);
        analytics.ActiveCount.Should().Be(3, "activeBengaluru, activeMumbai and oldNoBooking are all Active");
        analytics.BlockedCount.Should().Be(1);
        analytics.UnverifiedCount.Should().Be(1);
        analytics.SoftDeletedCount.Should().Be(1);
        analytics.NewToday.Should().Be(5, "every seeded customer except the backdated one registered today");
        analytics.NewLast7Days.Should().Be(5);
        analytics.NewInTrendWindow.Should().Be(5);
        analytics.CustomersWithBookings.Should().Be(1);
        analytics.CustomersWithZeroBookings.Should().Be(5);

        analytics.RegistrationTrend.Should().HaveCount(30, "zero-filled for every day in the trend window");
        analytics.RegistrationTrend.Last().Date.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
        analytics.RegistrationTrend.Last().Count.Should().Be(5);
        analytics.RegistrationTrend.Sum(p => p.Count).Should().Be(5, "the backdated customer falls outside the window entirely");

        analytics.TopCities.Should().ContainSingle(c => c.City == "Bengaluru" && c.Count == 2);
        analytics.TopCities.Should().ContainSingle(c => c.City == "Mumbai" && c.Count == 1);
    }

    [Fact]
    public async Task GetAnalyticsAsync_NoCustomers_ReturnsZeroedResponse()
    {
        await using var context = _database.CreateContext();
        var result = await CreateService(context).GetAnalyticsAsync(new CustomerAnalyticsRequest(TrendDays: 7));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCustomers.Should().Be(0);
        result.Value.CustomersWithBookings.Should().Be(0);
        result.Value.CustomersWithZeroBookings.Should().Be(0);
        result.Value.RegistrationTrend.Should().HaveCount(7);
        result.Value.RegistrationTrend.Should().OnlyContain(p => p.Count == 0);
        result.Value.TopCities.Should().BeEmpty();
    }

    private async Task BackdateCustomerCreatedAtAsync(Guid customerId, DateTime createdAtUtc)
    {
        await using var backdateContext = _database.CreateContext();
        await backdateContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE customer SET created_at = {createdAtUtc} WHERE id = {customerId}");
    }

    private static Booking CreateBooking(Guid customerId) =>
        new(
            Guid.NewGuid(),
            customerId,
            new CustomerSnapshot("Test Customer", "9000000000"),
            null,
            new AddressSnapshot("Home", "12 Main St", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Test Customer", "9000000000"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(11)),
            new PriceSnapshot(500m, 1, 500m, 0m, 0m, 500m, 18m, 90m, 10m, 600m));
}
