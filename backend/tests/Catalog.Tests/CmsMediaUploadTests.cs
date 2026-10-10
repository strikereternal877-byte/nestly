using FluentAssertions;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Cms;
using Nestly.Application.Storage;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Task 314: CMS media can now come from a genuine file upload
/// (<see cref="ICmsMediaService.SaveFileAsync"/>) as well as a hand-typed
/// URL. Uses a fake <see cref="IFileStorageService"/> rather than
/// <c>LocalDiskFileStorageService</c> against real disk - the storage
/// implementation is already this interface's one job and isn't what this
/// feature adds; what it adds is "the saved ref becomes a normal CmsMedia
/// row, same as <see cref="ICmsMediaService.CreateAsync"/> would."
/// </summary>
public class CmsMediaUploadTests : IDisposable
{
    private readonly TestDatabase _database = new();

    private static CmsMediaService BuildService(NestlyDbContext context, IFileStorageService fileStorage) =>
        new(new CmsMediaRepository(context), new AuditLogWriter(context, new StubAuditContextProvider()), fileStorage);

    [Fact]
    public async Task SaveFileAsync_returns_whatever_ref_the_storage_service_produces()
    {
        await using var context = _database.CreateContext();
        var storage = new FakeFileStorageService("/uploads/deterministic-test-ref.jpg");
        var service = BuildService(context, storage);

        var reference = await service.SaveFileAsync(Stream.Null, "banner.jpg", "image/jpeg");

        reference.Should().Be("/uploads/deterministic-test-ref.jpg");
        storage.LastFileNameHint.Should().Be("banner.jpg");
        storage.LastContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task An_uploaded_file_becomes_an_ordinary_CmsMedia_row_once_the_controller_calls_CreateAsync()
    {
        // Mirrors what CmsMediaController.Upload actually does: SaveFileAsync
        // for the ref, resolve it to an absolute URL (the controller's job,
        // not this service's - IFileStorageService knows nothing about
        // HTTP), then CreateAsync like any hand-typed URL would.
        await using var context = _database.CreateContext();
        var storage = new FakeFileStorageService("/uploads/some-guid.png");
        var service = BuildService(context, storage);

        var reference = await service.SaveFileAsync(Stream.Null, "logo.png", "image/png");
        var absoluteUrl = $"https://admin.example.test{reference}";
        var result = await service.CreateAsync(new CmsMediaCreateRequest(absoluteUrl, "Company logo", CmsMediaType.Image));

        result.IsSuccess.Should().BeTrue();
        result.Value.Url.Should().Be(absoluteUrl);
        result.Value.AltText.Should().Be("Company logo");

        var listed = await service.ListAsync();
        listed.Should().ContainSingle(m => m.Id == result.Value.Id && m.Url == absoluteUrl);
    }

    public void Dispose() => _database.Dispose();

    private sealed class FakeFileStorageService : IFileStorageService
    {
        private readonly string _refToReturn;
        public string? LastFileNameHint { get; private set; }
        public string? LastContentType { get; private set; }

        public FakeFileStorageService(string refToReturn) => _refToReturn = refToReturn;

        public Task<string> SaveAsync(Stream content, string fileNameHint, string contentType, CancellationToken cancellationToken = default)
        {
            LastFileNameHint = fileNameHint;
            LastContentType = contentType;
            return Task.FromResult(_refToReturn);
        }

        public Task DeleteAsync(string fileReference, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, Guid.NewGuid(), IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }
}
