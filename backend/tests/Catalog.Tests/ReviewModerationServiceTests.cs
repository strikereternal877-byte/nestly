using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Reviews;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 122: admin review moderation (SRS 12.15) - filtered search,
/// hide/unhide, flag/unflag (independent of hide/unhide), CSV export, and
/// that every moderation action lands in the shared audit trail rather than
/// mutating the review's original rating/text/tags.
/// </summary>
public sealed class ReviewModerationServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;
    private readonly Guid _moderatorId = Guid.NewGuid();

    public ReviewModerationServiceTests(TestDatabase db) => _db = db;

    private sealed record Fixture(Customer Customer, Category Category, Service Service, Guid BookingId);

    private Fixture SeedReview(NestlyDbContext context, int rating, string? reviewText, DateTime createdAtUtc, out Review review)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 999m);

        var booking = new Booking(
            Guid.NewGuid(), customer.Id,
            new CustomerSnapshot(customer.Name, customer.Mobile),
            null,
            new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(createdAtUtc), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0m, 0m, 999m, 0m, 0m, 0m, 999m));
        booking.AddItem(Guid.NewGuid(), service.Id, service.Name, service.Slug, 999m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        booking.TransitionTo(BookingStatus.Assigned);
        booking.TransitionTo(BookingStatus.InProgress);
        booking.TransitionTo(BookingStatus.Completed);

        context.Add(customer);
        context.Add(category);
        context.Add(service);
        context.Add(booking);
        context.SaveChanges();

        review = new Review(Guid.NewGuid(), booking.Id, customer.Id, service.Id, providerId: null, rating, reviewText);
        typeof(Review).GetProperty(nameof(Review.CreatedAtUtc))!.SetValue(review, createdAtUtc);
        context.Add(review);
        context.SaveChanges();

        return new Fixture(customer, category, service, booking.Id);
    }

    private ReviewModerationService BuildService(NestlyDbContext context) =>
        new(new ReviewRepository(context), new AuditLogWriter(context, new StubAuditContextProvider(_moderatorId)));

    [Fact]
    public async Task HideAsync_hides_the_review_but_never_touches_its_original_content()
    {
        Review review;
        Fixture fixture;
        using (var context = _db.CreateContext())
        {
            fixture = SeedReview(context, 2, "Terrible experience", DateTime.UtcNow, out review);
        }

        using var context2 = _db.CreateContext();
        var result = await BuildService(context2).HideAsync(review.Id, _moderatorId, new ModerateReviewRequest("Reported as spam"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ReviewStatus.Hidden);
        result.Value.ModeratorNote.Should().Be("Reported as spam");
        result.Value.ModeratedByAdminUserId.Should().Be(_moderatorId);

        // The original rating/text must survive the moderation action unchanged.
        result.Value.Rating.Should().Be(2);
        result.Value.ReviewText.Should().Be("Terrible experience");

        using var readContext = _db.CreateContext();
        var reloaded = await new ReviewRepository(readContext).GetByIdAsync(review.Id);
        reloaded!.Rating.Should().Be(2);
        reloaded.ReviewText.Should().Be("Terrible experience");
        reloaded.Status.Should().Be(ReviewStatus.Hidden);
    }

    [Fact]
    public async Task UnhideAsync_restores_visibility()
    {
        Review review;
        using (var context = _db.CreateContext())
        {
            SeedReview(context, 4, "Good", DateTime.UtcNow, out review);
        }

        using (var context2 = _db.CreateContext())
        {
            var hideResult = await BuildService(context2).HideAsync(review.Id, _moderatorId, new ModerateReviewRequest(null));
            hideResult.IsSuccess.Should().BeTrue();
        }

        using var context3 = _db.CreateContext();
        var result = await BuildService(context3).UnhideAsync(review.Id, _moderatorId, new ModerateReviewRequest("Reviewed, looks legitimate"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ReviewStatus.Visible);
    }

    [Fact]
    public async Task FlagAsync_is_independent_of_hide_state()
    {
        Review review;
        using (var context = _db.CreateContext())
        {
            SeedReview(context, 1, "Abusive language", DateTime.UtcNow, out review);
        }

        using var context2 = _db.CreateContext();
        var result = await BuildService(context2).FlagAsync(review.Id, _moderatorId, new ModerateReviewRequest("Contains profanity"));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsFlagged.Should().BeTrue();
        // Flagging alone does not hide the review - it stays visible until an
        // admin separately decides to hide it (SRS 12.15 lists them as
        // distinct actions).
        result.Value.Status.Should().Be(ReviewStatus.Visible);
    }

    [Fact]
    public async Task UnflagAsync_clears_the_flag_without_affecting_visibility()
    {
        Review review;
        using (var context = _db.CreateContext())
        {
            SeedReview(context, 3, "Mixed feelings", DateTime.UtcNow, out review);
        }

        using (var context2 = _db.CreateContext())
        {
            await BuildService(context2).FlagAsync(review.Id, _moderatorId, new ModerateReviewRequest(null));
        }

        using var context3 = _db.CreateContext();
        var result = await BuildService(context3).UnflagAsync(review.Id, _moderatorId, new ModerateReviewRequest("False positive"));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsFlagged.Should().BeFalse();
    }

    [Fact]
    public async Task HideAsync_returns_not_found_for_a_nonexistent_review()
    {
        using var context = _db.CreateContext();
        var result = await BuildService(context).HideAsync(Guid.NewGuid(), _moderatorId, new ModerateReviewRequest(null));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Review.NotFound");
    }

    [Fact]
    public async Task Every_moderation_action_is_recorded_in_the_shared_audit_trail()
    {
        Review review;
        using (var context = _db.CreateContext())
        {
            SeedReview(context, 5, "Excellent", DateTime.UtcNow, out review);
        }

        using (var context2 = _db.CreateContext())
        {
            await BuildService(context2).HideAsync(review.Id, _moderatorId, new ModerateReviewRequest("Under investigation"));
        }

        using var auditContext = _db.CreateContext();
        var auditRows = auditContext.Set<AuditLog>()
            .Where(a => a.EntityName == "Review" && a.EntityId == review.Id.ToString())
            .ToList();

        auditRows.Should().ContainSingle();
        auditRows[0].Action.Should().Be("Hidden");
        auditRows[0].ActorId.Should().Be(_moderatorId);
        auditRows[0].NewValues.Should().Contain("Hidden");
    }

    [Fact]
    public async Task SearchAsync_filters_by_status_flagged_rating_range_and_service()
    {
        Fixture fixture;
        Review visibleHighRating;
        Review hiddenLowRating;
        Review flaggedReview;
        using (var context = _db.CreateContext())
        {
            fixture = SeedReview(context, 5, "Great", DateTime.UtcNow.AddDays(-1), out visibleHighRating);
        }

        using (var context = _db.CreateContext())
        {
            SeedReview(context, 1, "Bad", DateTime.UtcNow.AddDays(-2), out hiddenLowRating);
        }

        using (var context = _db.CreateContext())
        {
            SeedReview(context, 3, "Questionable", DateTime.UtcNow.AddDays(-3), out flaggedReview);
        }

        using (var context = _db.CreateContext())
        {
            var service = BuildService(context);
            await service.HideAsync(hiddenLowRating.Id, _moderatorId, new ModerateReviewRequest(null));
        }

        using (var context = _db.CreateContext())
        {
            var service = BuildService(context);
            await service.FlagAsync(flaggedReview.Id, _moderatorId, new ModerateReviewRequest(null));
        }

        using var searchContext = _db.CreateContext();
        var searchService = BuildService(searchContext);

        var hiddenOnly = await searchService.SearchAsync(new ReviewModerationSearchRequest(Status: ReviewStatus.Hidden, IsFlagged: null, MinRating: null, MaxRating: null, FromUtc: null, ToUtc: null, ServiceId: null, CategoryId: null));
        hiddenOnly.IsSuccess.Should().BeTrue();
        hiddenOnly.Value.Items.Should().ContainSingle(x => x.Id == hiddenLowRating.Id);

        var flaggedOnly = await searchService.SearchAsync(new ReviewModerationSearchRequest(Status: null, IsFlagged: true, MinRating: null, MaxRating: null, FromUtc: null, ToUtc: null, ServiceId: null, CategoryId: null));
        flaggedOnly.Value.Items.Should().ContainSingle(x => x.Id == flaggedReview.Id);

        var highRatingOnly = await searchService.SearchAsync(new ReviewModerationSearchRequest(Status: null, IsFlagged: null, MinRating: 4, MaxRating: 5, FromUtc: null, ToUtc: null, ServiceId: null, CategoryId: null));
        highRatingOnly.Value.Items.Should().ContainSingle(x => x.Id == visibleHighRating.Id);

        var byService = await searchService.SearchAsync(new ReviewModerationSearchRequest(Status: null, IsFlagged: null, MinRating: null, MaxRating: null, FromUtc: null, ToUtc: null, ServiceId: fixture.Service.Id, CategoryId: null));
        byService.Value.Items.Should().ContainSingle(x => x.Id == visibleHighRating.Id);
        byService.Value.Items[0].CategoryName.Should().Be(fixture.Category.Name);
        byService.Value.Items[0].ServiceName.Should().Be(fixture.Service.Name);
        byService.Value.Items[0].CustomerName.Should().Be(fixture.Customer.Name);

        // Customer 360 view's "Reviews written" link (Customer Management UX
        // pass) - each SeedReview call creates its own distinct customer, so
        // filtering by fixture.Customer.Id must return only that one review.
        var byCustomer = await searchService.SearchAsync(new ReviewModerationSearchRequest(Status: null, IsFlagged: null, MinRating: null, MaxRating: null, FromUtc: null, ToUtc: null, ServiceId: null, CategoryId: null, CustomerId: fixture.Customer.Id));
        byCustomer.Value.Items.Should().ContainSingle(x => x.Id == visibleHighRating.Id);
    }

    [Fact]
    public async Task ExportCsvAsync_produces_a_header_row_plus_one_row_per_matching_review()
    {
        using (var context = _db.CreateContext())
        {
            SeedReview(context, 5, "Great, thanks!", DateTime.UtcNow, out _);
        }

        using var context2 = _db.CreateContext();
        var result = await BuildService(context2).ExportCsvAsync(new ReviewModerationSearchRequest(
            Status: null, IsFlagged: null, MinRating: null, MaxRating: null, FromUtc: null, ToUtc: null, ServiceId: null, CategoryId: null));

        result.IsSuccess.Should().BeTrue();
        string csv = System.Text.Encoding.UTF8.GetString(result.Value);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("ReviewId,BookingId,CustomerName");
        lines.Length.Should().BeGreaterThanOrEqualTo(2);
    }

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        private readonly Guid _actorId;

        public StubAuditContextProvider(Guid actorId) => _actorId = actorId;

        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, _actorId, IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }
}
