using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application.Routing;
using Nestly.Application.Storage;
using Nestly.Application.Tracking;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// A <see cref="IRouteEstimateProvider"/> that answers from a script instead
/// of the network. Every test in this project that can reach an ETA
/// computation goes through one of these - a real route lookup in a test suite
/// would be a billed HTTP call whose answer changes with the traffic.
/// </summary>
/// <remarks>
/// It also counts calls, which is the only way to prove a throttle: a
/// suppressed recompute and an allowed one that happened to return the same
/// number are indistinguishable from the stored row alone.
/// </remarks>
public sealed class StubRouteEstimateProvider : IRouteEstimateProvider
{
    private readonly Queue<RouteEstimate> _scripted = new();
    private RouteEstimate _default;

    public StubRouteEstimateProvider(int durationSeconds = 600, int distanceMetres = 4_000, RouteEstimateSource source = RouteEstimateSource.GoogleMaps)
    {
        _default = new RouteEstimate(0, distanceMetres, durationSeconds, source);
    }

    public int CallCount { get; private set; }

    /// <summary>Set when a test needs to model the seam returning an unusable response (task 266 says it never does; the ETA path must survive it anyway).</summary>
    public bool ReturnsNothing { get; set; }

    /// <summary>Queues the answer for the next call, so a test can watch an ETA move between two recomputes.</summary>
    public StubRouteEstimateProvider Then(int durationSeconds, int distanceMetres = 4_000, RouteEstimateSource source = RouteEstimateSource.GoogleMaps)
    {
        _scripted.Enqueue(new RouteEstimate(0, distanceMetres, durationSeconds, source));
        return this;
    }

    public void Returns(int durationSeconds, int distanceMetres = 4_000, RouteEstimateSource source = RouteEstimateSource.GoogleMaps)
    {
        _default = new RouteEstimate(0, distanceMetres, durationSeconds, source);
    }

    public Task<IReadOnlyList<RouteEstimate>> EstimateAsync(
        GeoCoordinate origin,
        IReadOnlyList<GeoCoordinate> destinations,
        CancellationToken cancellationToken = default)
    {
        CallCount++;

        if (ReturnsNothing)
        {
            return Task.FromResult<IReadOnlyList<RouteEstimate>>([]);
        }

        var estimate = _scripted.Count > 0 ? _scripted.Dequeue() : _default;
        return Task.FromResult<IReadOnlyList<RouteEstimate>>([estimate]);
    }
}

/// <summary>
/// An <see cref="IBookingEtaService"/> that does nothing, for the tests of
/// other services that merely have to construct
/// <see cref="ProviderJobService"/>/<see cref="ProviderLocationIngestService"/>
/// and have no interest in arrival estimates.
/// </summary>
public sealed class NoOpBookingEtaService : IBookingEtaService
{
    public Task RefreshAsync(Guid bookingId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ClearAsync(Guid bookingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// An <see cref="IFileStorageService"/> that does nothing, for the tests of
/// other services that merely have to construct <see cref="ProviderJobService"/>
/// and have no interest in completion-photo storage.
/// </summary>
public sealed class NoOpFileStorageService : IFileStorageService
{
    public Task<string> SaveAsync(Stream content, string fileNameHint, string contentType, CancellationToken cancellationToken = default) =>
        Task.FromResult("/uploads/test-stub.jpg");

    public Task DeleteAsync(string fileReference, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public static class BookingEtaTestFactory
{
    /// <summary>The real service over the test database, with only the network seam stubbed.</summary>
    public static BookingEtaService CreateEtaService(
        NestlyDbContext context,
        IRouteEstimateProvider routeEstimateProvider,
        BookingEtaOptions? options = null) => new(
        new BookingRepository(context),
        new BookingTrackingRepository(context),
        new ProviderLocationPingRepository(context),
        routeEstimateProvider,
        new SandboxRouteEstimateProvider(Options.Create(new SandboxRouteEstimateOptions())),
        Options.Create(options ?? new BookingEtaOptions()),
        NullLogger<BookingEtaService>.Instance);
}
