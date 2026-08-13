using FluentAssertions;
using Nestly.Application.Storage;
using Xunit;

namespace Nestly.Catalog.Tests;

/// <summary>
/// <see cref="FileReferenceUrl.ToAbsolute"/> is the one thing standing
/// between CmsMediaController/JobsController and building a broken URL like
/// "https://admin.example.test/https://xxxx.supabase.co/..." once
/// <c>IFileStorageService</c> can return either an origin-relative path
/// (local disk) or an already-absolute URL (Supabase).
/// </summary>
public class FileReferenceUrlTests
{
    [Fact]
    public void Relative_reference_is_prefixed_with_the_request_origin()
    {
        var result = FileReferenceUrl.ToAbsolute("/uploads/some-guid.png", "https", "admin.example.test");

        result.Should().Be("https://admin.example.test/uploads/some-guid.png");
    }

    [Theory]
    [InlineData("https://xxxx.supabase.co/storage/v1/object/public/uploads/some-guid.png")]
    [InlineData("http://xxxx.supabase.co/storage/v1/object/public/uploads/some-guid.png")]
    public void Absolute_reference_is_returned_unchanged(string absoluteRef)
    {
        var result = FileReferenceUrl.ToAbsolute(absoluteRef, "https", "admin.example.test");

        result.Should().Be(absoluteRef);
    }
}
