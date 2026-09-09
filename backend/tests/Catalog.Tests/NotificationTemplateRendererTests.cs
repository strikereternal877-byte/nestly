using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Nestly.Domain;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 87b (template rendering with variables) plus tasks 126a-d
/// (the renderer's move from a fixed built-in dictionary to the admin-managed,
/// DB-backed <see cref="FakeNotificationTemplateRepository"/> - seeded with
/// the exact same content <c>NotificationTemplateSeedData</c> gives a fresh
/// database, so this suite's expectations are unchanged from before the move).
/// </summary>
public sealed class NotificationTemplateRendererTests
{
    private static NotificationTemplateRenderer BuildRenderer() =>
        new(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task RenderAsync_substitutes_variables_into_the_sms_body()
    {
        var renderer = BuildRenderer();

        var rendered = await renderer.RenderAsync(
            NotificationEventType.Welcome, NotificationChannel.Sms, new Dictionary<string, string> { ["CustomerName"] = "Asha" });

        rendered.Body.Should().Contain("Asha");
        rendered.Subject.Should().BeNull("SMS has no subject line");
        rendered.TemplateKey.Should().Be("welcome_sms");
    }

    [Fact]
    public async Task RenderAsync_substitutes_variables_into_both_email_subject_and_body()
    {
        var renderer = BuildRenderer();

        var rendered = await renderer.RenderAsync(
            NotificationEventType.BookingConfirmed, NotificationChannel.Email,
            new Dictionary<string, string> { ["CustomerName"] = "Asha", ["ServiceName"] = "Deep Clean", ["SlotDate"] = "2026-08-01", ["SlotWindow"] = "Morning", ["TotalPayable"] = "999" });

        rendered.Subject.Should().Be("Your Glavyx booking is confirmed");
        rendered.Body.Should().Contain("Deep Clean").And.Contain("999");
    }

    [Fact]
    public async Task RenderAsync_leaves_an_unresolved_placeholder_untouched_rather_than_throwing()
    {
        var renderer = BuildRenderer();

        var rendered = await renderer.RenderAsync(NotificationEventType.Welcome, NotificationChannel.Sms, new Dictionary<string, string>());

        rendered.Body.Should().Contain("{{CustomerName}}");
    }

    [Fact]
    public async Task RenderAsync_throws_for_an_event_channel_combination_with_no_template()
    {
        var renderer = BuildRenderer();

        var act = () => renderer.RenderAsync(NotificationEventType.Welcome, (NotificationChannel)999, new Dictionary<string, string>());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(NotificationEventType.Welcome)]
    [InlineData(NotificationEventType.BookingConfirmed)]
    [InlineData(NotificationEventType.PaymentSuccess)]
    [InlineData(NotificationEventType.PaymentFailed)]
    [InlineData(NotificationEventType.BookingCancelled)]
    [InlineData(NotificationEventType.BookingRescheduled)]
    [InlineData(NotificationEventType.RefundProcessed)]
    [InlineData(NotificationEventType.SupportTicketUpdate)]
    public async Task Every_trigger_event_has_both_an_sms_and_an_email_template(NotificationEventType eventType)
    {
        var renderer = BuildRenderer();

        (await renderer.SupportsChannelAsync(eventType, NotificationChannel.Sms)).Should().BeTrue();
        (await renderer.SupportsChannelAsync(eventType, NotificationChannel.Email)).Should().BeTrue();
    }
}
