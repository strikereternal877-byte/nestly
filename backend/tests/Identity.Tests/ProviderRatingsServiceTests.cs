using FluentAssertions;
using Nestly.Application;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// docs/OPEN-FIXES-FEATURES.csv "Ratings and feedback": the provider's own
/// self-service view of their running rating and recent reviews. Reuses the
/// real <see cref="Review"/> entity/<see cref="ReviewRepository"/> that
/// <c>ProviderPerformanceServiceTests</c>/<c>ProviderProfileServiceTests</c>
/// already exercise for the admin-side aggregate - this suite covers the
/// provider-facing surface's own added behaviour: IDOR scoping, moderation
/// exclusion, PII minimization, and paging.
/// </summary>
public class ProviderRatingsServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Guid _providerId;
    private readonly Guid _otherProviderId;
    private readonly Guid _serviceId;
    private readonly Guid _adminUserId = Guid.NewGuid();
    private Customer _customer = null!;

    public ProviderRatingsServiceTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        var otherProvider = new Provider(Guid.NewGuid(), "Meena Iyer", "Meena's Services", ProviderType.Individual, "+919876500000");
        _providerId = provider.Id;
        _otherProviderId = otherProvider.Id;

        var category = new Category(Guid.NewGuid(), "Cleaning " + Guid.NewGuid().ToString("N")[..6], "cleaning-" + Guid.NewGuid().ToString("N")[..6], "Home cleaning services");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid().ToString("N")[..6], "desc", 500m);
        _serviceId = service.Id;

        _customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Priya Nair", CustomerStatus.Active);

        context.AddRange(provider, otherProvider, category, service, _customer);
        context.SaveChanges();
    }

    private static ProviderRatingsService CreateService(NestlyDbContext context) => new(new ReviewRepository(context));

    /// <summary>
    /// Review.BookingId is uniquely indexed (one primary review per booking),
    /// so every review needs its own real booking row - mirrors
    /// ProviderProfileServiceTests'/ProviderPhotoAndRatingTests' fixture shape.
    /// </summary>
    private Booking NewBooking(NestlyDbContext context, Guid customerId, string customerName)
    {
        var booking = new Booking(
            Guid.NewGuid(), customerId,
            new CustomerSnapshot(customerName, "9876543210"),
            null,
            new AddressSnapshot("Home", "12 MG Road", null, null, "560001", "Bengaluru", "Karnataka", 12.97m, 77.59m, customerName, "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(500m, 1, 500m, 0, 0, 500m, 0, 0, 0, 500m));
        context.Add(booking);
        return booking;
    }

    private Review AddReview(NestlyDbContext context, Guid providerId, Guid customerId, string customerName, int rating, string? reviewText = null)
    {
        var booking = NewBooking(context, customerId, customerName);
        var review = new Review(Guid.NewGuid(), booking.Id, customerId, _serviceId, providerId, rating, reviewText);
        context.Add(review);
        context.SaveChanges();
        return review;
    }

    private Guid AddCustomer(NestlyDbContext context, string name)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], name, CustomerStatus.Active);
        context.Add(customer);
        context.SaveChanges();
        return customer.Id;
    }

    [Fact]
    public async Task GetSummaryAsync_returns_null_average_when_the_provider_has_no_reviews_yet()
    {
        await using var context = _database.CreateContext();

        var result = await CreateService(context).GetSummaryAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.AverageRating.Should().BeNull("a new professional with no reviews is not the same as one rated zero");
        result.Value.ReviewCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_averages_only_the_callers_own_visible_reviews()
    {
        await using var context = _database.CreateContext();
        AddReview(context, _providerId, _customer.Id, _customer.Name, rating: 4);
        AddReview(context, _providerId, _customer.Id, _customer.Name, rating: 5);
        AddReview(context, _otherProviderId, _customer.Id, _customer.Name, rating: 1);

        var result = await CreateService(context).GetSummaryAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.AverageRating.Should().Be(4.5);
        result.Value.ReviewCount.Should().Be(2);
    }

    [Fact]
    public async Task GetSummaryAsync_excludes_hidden_reviews()
    {
        await using var context = _database.CreateContext();
        AddReview(context, _providerId, _customer.Id, _customer.Name, rating: 5);
        var hidden = AddReview(context, _providerId, _customer.Id, _customer.Name, rating: 1);
        hidden.Hide(_adminUserId, "Abusive content");
        context.Update(hidden);
        context.SaveChanges();

        var result = await CreateService(context).GetSummaryAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.AverageRating.Should().Be(5, "a hidden review is hidden from the rating too, or moderation would be cosmetic");
        result.Value.ReviewCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReviewsAsync_scopes_results_to_the_callers_own_reviews()
    {
        await using var context = _database.CreateContext();
        AddReview(context, _providerId, _customer.Id, _customer.Name, rating: 4, "Great work");
        AddReview(context, _otherProviderId, _customer.Id, _customer.Name, rating: 1, "Not for you");

        var result = await CreateService(context).GetReviewsAsync(_providerId, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items[0].ReviewText.Should().Be("Great work");
    }

    [Fact]
    public async Task GetReviewsAsync_excludes_hidden_reviews()
    {
        await using var context = _database.CreateContext();
        var hidden = AddReview(context, _providerId, _customer.Id, _customer.Name, rating: 1, "Rude and late");
        hidden.Hide(_adminUserId, "Abusive content");
        context.Update(hidden);
        context.SaveChanges();

        var result = await CreateService(context).GetReviewsAsync(_providerId, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
    }

    /// <summary>
    /// A flagged review can otherwise still be publicly Visible (Review.IsFlagged
    /// is orthogonal to Status), but it must not reach the provider it describes
    /// until a moderator resolves the flag.
    /// </summary>
    [Fact]
    public async Task GetReviewsAsync_excludes_flagged_reviews_even_when_still_visible()
    {
        await using var context = _database.CreateContext();
        var flagged = AddReview(context, _providerId, _customer.Id, _customer.Name, rating: 1, "Contains a slur");
        flagged.Flag(_adminUserId, "Reported for abusive language");
        context.Update(flagged);
        context.SaveChanges();

        flagged.Status.Should().Be(ReviewStatus.Visible, "flagging does not by itself hide a review");

        var result = await CreateService(context).GetReviewsAsync(_providerId, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task GetReviewsAsync_shows_only_the_customers_first_name()
    {
        await using var context = _database.CreateContext();
        AddReview(context, _providerId, _customer.Id, _customer.Name, rating: 5, "Excellent");

        var result = await CreateService(context).GetReviewsAsync(_providerId, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].CustomerDisplayName.Should().Be("Priya", "the provider must not see the customer's full name");
    }

    [Fact]
    public async Task GetReviewsAsync_falls_back_to_a_customer_placeholder_for_a_blank_name()
    {
        await using var context = _database.CreateContext();
        var blankNameCustomerId = AddCustomer(context, " ");
        AddReview(context, _providerId, blankNameCustomerId, " ", rating: 3, "Fine");

        var result = await CreateService(context).GetReviewsAsync(_providerId, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].CustomerDisplayName.Should().Be("A customer");
    }

    [Fact]
    public async Task GetReviewsAsync_orders_reviews_newest_first()
    {
        await using var context = _database.CreateContext();
        var older = AddReview(context, _providerId, _customer.Id, _customer.Name, rating: 3, "Okay");
        var newer = AddReview(context, _providerId, _customer.Id, _customer.Name, rating: 5, "Great");

        var result = await CreateService(context).GetReviewsAsync(_providerId, page: 1, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.Items[0].Id.Should().Be(newer.Id);
        result.Value.Items[1].Id.Should().Be(older.Id);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(10_000)]
    public async Task GetReviewsAsync_caps_an_oversized_page_size(int requestedPageSize)
    {
        await using var context = _database.CreateContext();

        var result = await CreateService(context).GetReviewsAsync(_providerId, page: 1, pageSize: requestedPageSize);

        result.IsSuccess.Should().BeTrue();
        result.Value.PageSize.Should().Be(100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetReviewsAsync_normalizes_a_non_positive_page_to_the_first_page(int requestedPage)
    {
        await using var context = _database.CreateContext();

        var result = await CreateService(context).GetReviewsAsync(_providerId, page: requestedPage, pageSize: 20);

        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(1);
    }

    public void Dispose() => _database.Dispose();
}
