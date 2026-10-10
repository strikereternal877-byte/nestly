using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Coupons;
using Nestly.Application.CustomerRatings;
using Nestly.Application.Customers;
using Nestly.Application.Support;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Admin customer management (SRS 12.4, tasks 101a-101d). Composes the
/// existing Booking/Wallet/Coupon/SupportTicket repositories rather than
/// introducing new tables for data that already lives in those modules -
/// only <see cref="CustomerNote"/> (task 101d) is genuinely new.
/// </summary>
public class CustomerManagementService : ICustomerManagementService
{
    private readonly ICustomerRepository _customerRepository;
    private readonly ICustomerAddressRepository _addressRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IWalletLedgerRepository _walletLedgerRepository;
    private readonly IWalletService _walletService;
    private readonly ICouponRedemptionRepository _couponRedemptionRepository;
    private readonly ICouponRepository _couponRepository;
    private readonly ISupportTicketRepository _supportTicketRepository;
    private readonly ICustomerNoteRepository _noteRepository;
    private readonly ICustomerRatingRepository _customerRatingRepository;
    private readonly ICustomerSessionRepository _sessionRepository;

    /// <summary>Recent ratings shown on the Customer 360 view - a risk signal, not a full history, same cap rationale as ReviewsSummary's 5-most-recent on the service page.</summary>
    private const int RecentRatingsLimit = 10;

    private static readonly IReadOnlyList<BookingStatus> AllBookingStatuses = Enum.GetValues<BookingStatus>();

    public CustomerManagementService(
        ICustomerRepository customerRepository,
        ICustomerAddressRepository addressRepository,
        IBookingRepository bookingRepository,
        IWalletLedgerRepository walletLedgerRepository,
        IWalletService walletService,
        ICouponRedemptionRepository couponRedemptionRepository,
        ICouponRepository couponRepository,
        ISupportTicketRepository supportTicketRepository,
        ICustomerNoteRepository noteRepository,
        ICustomerRatingRepository customerRatingRepository,
        ICustomerSessionRepository sessionRepository)
    {
        _customerRepository = customerRepository;
        _addressRepository = addressRepository;
        _bookingRepository = bookingRepository;
        _walletLedgerRepository = walletLedgerRepository;
        _walletService = walletService;
        _couponRedemptionRepository = couponRedemptionRepository;
        _couponRepository = couponRepository;
        _supportTicketRepository = supportTicketRepository;
        _noteRepository = noteRepository;
        _customerRatingRepository = customerRatingRepository;
        _sessionRepository = sessionRepository;
    }

    public async Task<Result<CustomerSearchResponse>> SearchAsync(CustomerSearchRequest request)
    {
        var filter = new CustomerSearchFilter(
            request.Name,
            request.Mobile,
            request.Email,
            request.City,
            request.Status,
            request.RegisteredFromUtc,
            request.RegisteredToUtc,
            request.MinBookingCount,
            request.MaxBookingCount,
            request.Page,
            request.PageSize);

        var result = await _customerRepository.SearchAsync(filter);

        var items = result.Rows
            .Select(row => new CustomerSummaryResponse(
                row.Customer.Id,
                row.Customer.Name,
                row.Customer.Mobile,
                row.Customer.Email,
                row.Customer.City,
                row.Customer.Status,
                row.Customer.CreatedAt,
                row.BookingCount))
            .ToList();

        return new CustomerSearchResponse(items, result.TotalCount, request.Page, request.PageSize);
    }

    public async Task<Result<CustomerDetailResponse>> GetDetailAsync(Guid customerId)
    {
        var customer = await _customerRepository.GetByIdAsync(customerId);
        if (customer is null)
        {
            return Error.NotFound("Customer.NotFound", "Customer was not found.");
        }

        return await BuildDetailAsync(customer);
    }

    public async Task<Result<CustomerDetailResponse>> BlockAsync(Guid customerId, Guid adminUserId, string reason)
    {
        var customer = await _customerRepository.GetByIdAsync(customerId);
        if (customer is null)
        {
            return Error.NotFound("Customer.NotFound", "Customer was not found.");
        }

        if (customer.Status == CustomerStatus.SoftDeleted)
        {
            return Error.Business("Customer.CannotBlockDeleted", "A deleted customer account cannot be blocked.");
        }

        if (customer.Status != CustomerStatus.Blocked)
        {
            customer.UpdateStatus(CustomerStatus.Blocked);
            await _customerRepository.UpdateAsync(customer);
            await _noteRepository.AddAsync(new CustomerNote(Guid.NewGuid(), customerId, adminUserId, $"Account blocked. Reason: {reason}"));
        }

        return await BuildDetailAsync(customer);
    }

    public async Task<Result<CustomerDetailResponse>> UnblockAsync(Guid customerId, Guid adminUserId)
    {
        var customer = await _customerRepository.GetByIdAsync(customerId);
        if (customer is null)
        {
            return Error.NotFound("Customer.NotFound", "Customer was not found.");
        }

        if (customer.Status != CustomerStatus.Blocked)
        {
            return Error.Business("Customer.NotBlocked", "This customer's account is not currently blocked.");
        }

        customer.UpdateStatus(CustomerStatus.Active);
        await _customerRepository.UpdateAsync(customer);
        await _noteRepository.AddAsync(new CustomerNote(Guid.NewGuid(), customerId, adminUserId, "Account unblocked."));

        return await BuildDetailAsync(customer);
    }

    public async Task<Result<CustomerDetailResponse>> DeleteAsync(Guid customerId, Guid adminUserId, string reason)
    {
        var customer = await _customerRepository.GetByIdAsync(customerId);
        if (customer is null)
        {
            return Error.NotFound("Customer.NotFound", "Customer was not found.");
        }

        if (customer.Status == CustomerStatus.SoftDeleted)
        {
            return Error.Business("Customer.AlreadyDeleted", "This customer's account has already been deleted.");
        }

        customer.SoftDelete();
        await _customerRepository.UpdateAsync(customer);
        await _sessionRepository.RevokeAllForCustomerAsync(customerId);
        await _noteRepository.AddAsync(new CustomerNote(Guid.NewGuid(), customerId, adminUserId, $"Account deleted. Reason: {reason}"));

        return await BuildDetailAsync(customer);
    }

    public async Task<Result<CustomerDetailResponse>> AdjustWalletAsync(Guid customerId, Guid adminUserId, AdjustCustomerWalletRequest request)
    {
        var customer = await _customerRepository.GetByIdAsync(customerId);
        if (customer is null)
        {
            return Error.NotFound("Customer.NotFound", "Customer was not found.");
        }

        string action = request.Direction == WalletEntryType.Credit ? "credited" : "debited";
        var description = $"Manual {action} by admin. Reason: {request.Reason}";

        if (request.Direction == WalletEntryType.Credit)
        {
            await _walletService.CreditAsync(customerId, request.Amount, WalletSourceType.ManualAdjustment, null, description);
        }
        else
        {
            var debitResult = await _walletService.DebitAsync(customerId, request.Amount, WalletSourceType.ManualAdjustment, null, description);
            if (debitResult.IsFailure)
            {
                return debitResult.Error;
            }
        }

        await _noteRepository.AddAsync(new CustomerNote(Guid.NewGuid(), customerId, adminUserId, $"Wallet {action}: {request.Amount:0.00}. Reason: {request.Reason}"));

        return await BuildDetailAsync(customer);
    }

    public async Task<Result<CustomerNoteResponse>> AddNoteAsync(Guid customerId, Guid adminUserId, string note)
    {
        if (!await _customerRepository.ExistsAsync(customerId))
        {
            return Error.NotFound("Customer.NotFound", "Customer was not found.");
        }

        var entry = new CustomerNote(Guid.NewGuid(), customerId, adminUserId, note);
        await _noteRepository.AddAsync(entry);

        return new CustomerNoteResponse(entry.Id, entry.AuthorAdminUserId, entry.Note, entry.CreatedAtUtc);
    }

    private async Task<CustomerDetailResponse> BuildDetailAsync(Customer customer)
    {
        var addresses = await _addressRepository.GetByCustomerAsync(customer.Id);
        var bookings = await _bookingRepository.ListByCustomerAsync(customer.Id, AllBookingStatuses);
        var walletEntries = await _walletLedgerRepository.ListByCustomerAsync(customer.Id);
        var redemptions = await _couponRedemptionRepository.ListByCustomerAsync(customer.Id);
        var supportTickets = await _supportTicketRepository.ListByCustomerAsync(customer.Id);
        var notes = await _noteRepository.ListByCustomerAsync(customer.Id);
        var ratingSummary = await _customerRatingRepository.GetSummaryForCustomerAsync(customer.Id);
        var recentRatings = await _customerRatingRepository.ListRecentForCustomerAsync(customer.Id, RecentRatingsLimit);

        decimal walletBalance = walletEntries.Count > 0 ? walletEntries[0].BalanceAfter : 0m;

        // Task 258: the cache only avoided re-querying a coupon already seen
        // on this customer, so each distinct coupon still cost its own round
        // trip (and loaded a whole Coupon aggregate to read its code).
        var couponCodes = await _couponRepository.GetCodesByIdsAsync(
            redemptions.Select(r => r.CouponId).Distinct().ToList());

        var coupons = redemptions
            .Select(redemption => new CustomerCouponUsageResponse(
                redemption.CouponId,
                couponCodes.GetValueOrDefault(redemption.CouponId, "(deleted coupon)"),
                redemption.BookingId,
                redemption.DiscountAmount,
                redemption.RedeemedAtUtc))
            .ToList();

        return new CustomerDetailResponse(
            customer.Id,
            customer.Name,
            customer.Mobile,
            customer.Email,
            customer.DateOfBirth,
            customer.City,
            customer.State,
            customer.Pincode,
            customer.Country,
            customer.Status,
            customer.CreatedAt,
            addresses.Select(a => new CustomerAddressResponse(
                a.Id, a.Label, a.Line1, a.Line2, a.Landmark, a.Pincode, a.City, a.State, a.IsDefault)).ToList(),
            bookings.Select(b => new CustomerBookingResponse(
                b.Id, b.Status, b.SlotDate, b.TotalPayableSnapshot, b.CreatedAtUtc)).ToList(),
            walletBalance,
            walletEntries.Select(w => new CustomerWalletEntryResponse(
                w.Id, w.EntryType, w.Amount, w.BalanceAfter, w.SourceType, w.Description, w.CreatedAtUtc)).ToList(),
            coupons,
            supportTickets.Select(t => new CustomerSupportTicketResponse(
                t.Id, t.Category, t.Priority, t.Subject, t.Status, t.CreatedAtUtc)).ToList(),
            notes.Select(n => new CustomerNoteResponse(n.Id, n.AuthorAdminUserId, n.Note, n.CreatedAtUtc)).ToList(),
            new CustomerProviderRatingsResponse(ratingSummary?.AverageRating, ratingSummary?.RatingCount ?? 0, recentRatings));
    }

    /// <inheritdoc/>
    public async Task<Result<CustomerAnalyticsResponse>> GetAnalyticsAsync(CustomerAnalyticsRequest request)
    {
        var counts = await _customerRepository.GetAnalyticsCountsAsync(request.TrendDays);

        return new CustomerAnalyticsResponse(
            request.TrendDays,
            counts.TotalCustomers,
            counts.ActiveCount,
            counts.BlockedCount,
            counts.UnverifiedCount,
            counts.SoftDeletedCount,
            counts.NewToday,
            counts.NewLast7Days,
            counts.NewInTrendWindow,
            counts.CustomersWithBookings,
            CustomersWithZeroBookings: counts.TotalCustomers - counts.CustomersWithBookings,
            counts.RegistrationTrend,
            counts.TopCities);
    }
}
