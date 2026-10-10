using Nestly.Application.Storage;
using Nestly.Application.Tracking;

namespace Nestly.Catalog.Tests;

/// <summary>
/// An <see cref="IBookingEtaService"/> that does nothing (task 271), for the
/// tests of other services that merely have to construct a collaborator taking
/// one and have no interest in arrival estimates. The ETA behaviour itself is
/// covered by <c>BookingEtaServiceTests</c> in Identity.Tests, next to the
/// location-ingest tests it hangs off.
/// </summary>
public sealed class NoOpBookingEtaService : IBookingEtaService
{
    public Task RefreshAsync(Guid bookingId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ClearAsync(Guid bookingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// An <see cref="IFileStorageService"/> that does nothing, for the tests of
/// other services that merely have to construct <see cref="Nestly.Infrastructure.Services.ProviderJobService"/>
/// and have no interest in completion-photo storage.
/// </summary>
public sealed class NoOpFileStorageService : IFileStorageService
{
    public Task<string> SaveAsync(Stream content, string fileNameHint, string contentType, CancellationToken cancellationToken = default) =>
        Task.FromResult("/uploads/test-stub.jpg");

    public Task DeleteAsync(string fileReference, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
