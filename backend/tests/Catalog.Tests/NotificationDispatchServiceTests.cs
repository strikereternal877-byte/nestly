using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Nestly.Application;
using Nestly.Application.Notifications;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 87c-d (channel dispatch SMS/email, delivery event logging/tracking) and 156 (push channel dispatch).</summary>
public sealed class NotificationDispatchServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public NotificationDispatchServiceTests(TestDatabase db) => _db = db;

    /// <summary>Hand-rolled fake matching this test project's existing no-mocking-library convention (see InMemoryCacheService).</summary>
    private sealed class FakeNotificationProvider : INotificationProvider
    {
        public bool FailSms { get; set; }
        public bool FailEmail { get; set; }
        public List<(string To, string Message)> SentSms { get; } = [];
        public List<(string To, string Subject, string Body)> SentEmails { get; } = [];

        public Task<Result> SendSmsAsync(string toMobile, string message, CancellationToken cancellationToken = default)
        {
            if (FailSms)
            {
                return Task.FromResult(Result.Failure(Error.Infrastructure("Sms.Failed", "Simulated SMS failure.")));
            }

            SentSms.Add((toMobile, message));
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SendEmailAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
        {
            if (FailEmail)
            {
                return Task.FromResult(Result.Failure(Error.Infrastructure("Email.Failed", "Simulated email failure.")));
            }

            SentEmails.Add((toEmail, subject, body));
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FakePushNotificationProvider : IPushNotificationProvider
    {
        public List<(string DeviceToken, string Title, string Body)> SentPushes { get; } = [];

        public Task<Result> SendPushAsync(string deviceToken, string title, string body, CancellationToken cancellationToken = default)
        {
            SentPushes.Add((deviceToken, title, body));
            return Task.FromResult(Result.Success());
        }
    }

    private Guid SeedCustomer(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        context.Add(customer);
        context.SaveChanges();
        return customer.Id;
    }

    private Guid SeedProvider(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "9" + Guid.NewGuid().ToString("N")[..9]);
        context.Add(provider);
        context.SaveChanges();
        return provider.Id;
    }

    /// <summary>
    /// Task 277: the call the fulfilment notification chain waits on - before
    /// this method existed, no code path could address a provider at all (see
    /// BookingNotificationTriggerHandler's doc comment on why the customer/
    /// device-token lookups moved here).
    /// </summary>
    [Fact]
    public async Task ResolveRecipientAsync_resolves_a_provider_owner_from_the_provider_table_not_the_customer_table()
    {
        Guid providerId;
        using (var context = _db.CreateContext())
        {
            providerId = SeedProvider(context);
        }

        using (var tokenContext = _db.CreateContext())
        {
            await new DeviceTokenRepository(tokenContext).AddAsync(
                new DeviceToken(Guid.NewGuid(), DeviceTokenOwner.ForProvider(providerId), DevicePlatform.Fcm, "provider-token-" + Guid.NewGuid()));
        }

        using var dispatchContext = _db.CreateContext();
        var service = new NotificationDispatchService(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())),
            new FakeNotificationProvider(), new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
            new NotificationEventRepository(dispatchContext), new DeviceTokenRepository(dispatchContext),
            new CustomerRepository(dispatchContext), new ProviderRepository(dispatchContext),
            new NoOpMetricsService(), NullLogger<NotificationDispatchService>.Instance);

        var recipient = await service.ResolveRecipientAsync(DeviceTokenOwner.ForProvider(providerId));

        recipient.Mobile.Should().NotBeNullOrEmpty();
        recipient.PushDeviceTokens.Should().ContainSingle(t => t.StartsWith("provider-token-"));
    }

    [Fact]
    public async Task DispatchAsync_sends_and_logs_both_channels_when_both_contacts_are_present()
    {
        Guid customerId;
        using (var context = _db.CreateContext())
        {
            customerId = SeedCustomer(context);
        }

        var provider = new FakeNotificationProvider();
        using var dispatchContext = _db.CreateContext();
        var service = new NotificationDispatchService(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())), provider, new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance), new NotificationEventRepository(dispatchContext), new DeviceTokenRepository(dispatchContext), new CustomerRepository(dispatchContext), new ProviderRepository(dispatchContext), new NoOpMetricsService(), NullLogger<NotificationDispatchService>.Instance);

        var outcomes = await service.DispatchAsync(
            customerId, NotificationEventType.Welcome, new NotificationRecipient("9876543210", "asha@example.com"),
            new Dictionary<string, string> { ["CustomerName"] = "Asha" });

        outcomes.Should().HaveCount(2);
        outcomes.Should().OnlyContain(o => o.Status == NotificationDeliveryStatus.Sent);
        provider.SentSms.Should().ContainSingle(s => s.Message.Contains("Asha"));
        provider.SentEmails.Should().ContainSingle(e => e.Subject == "Welcome to Glavyx");

        using var readContext = _db.CreateContext();
        var logged = await new NotificationEventRepository(readContext).ListByCustomerAsync(customerId);
        logged.Should().HaveCount(2);
        logged.Should().OnlyContain(n => n.Status == NotificationDeliveryStatus.Sent && n.SentAtUtc != null);
    }

    [Fact]
    public async Task DispatchAsync_only_dispatches_channels_with_a_contact()
    {
        Guid customerId;
        using (var context = _db.CreateContext())
        {
            customerId = SeedCustomer(context);
        }

        var provider = new FakeNotificationProvider();
        using var dispatchContext = _db.CreateContext();
        var service = new NotificationDispatchService(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())), provider, new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance), new NotificationEventRepository(dispatchContext), new DeviceTokenRepository(dispatchContext), new CustomerRepository(dispatchContext), new ProviderRepository(dispatchContext), new NoOpMetricsService(), NullLogger<NotificationDispatchService>.Instance);

        var outcomes = await service.DispatchAsync(
            customerId, NotificationEventType.Welcome, new NotificationRecipient("9876543210", Email: null),
            new Dictionary<string, string> { ["CustomerName"] = "Asha" });

        outcomes.Should().ContainSingle(o => o.Channel == NotificationChannel.Sms);
        provider.SentEmails.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_logs_a_failed_channel_without_aborting_the_other()
    {
        Guid customerId;
        using (var context = _db.CreateContext())
        {
            customerId = SeedCustomer(context);
        }

        var provider = new FakeNotificationProvider { FailSms = true };
        using var dispatchContext = _db.CreateContext();
        var service = new NotificationDispatchService(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())), provider, new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance), new NotificationEventRepository(dispatchContext), new DeviceTokenRepository(dispatchContext), new CustomerRepository(dispatchContext), new ProviderRepository(dispatchContext), new NoOpMetricsService(), NullLogger<NotificationDispatchService>.Instance);

        var outcomes = await service.DispatchAsync(
            customerId, NotificationEventType.Welcome, new NotificationRecipient("9876543210", "asha@example.com"),
            new Dictionary<string, string> { ["CustomerName"] = "Asha" });

        outcomes.Should().HaveCount(2);
        outcomes.Single(o => o.Channel == NotificationChannel.Sms).Status.Should().Be(NotificationDeliveryStatus.Failed);
        outcomes.Single(o => o.Channel == NotificationChannel.Email).Status.Should().Be(NotificationDeliveryStatus.Sent);
        provider.SentEmails.Should().ContainSingle();
    }

    [Fact]
    public async Task DispatchAsync_stores_a_masked_recipient_never_the_raw_contact()
    {
        Guid customerId;
        using (var context = _db.CreateContext())
        {
            customerId = SeedCustomer(context);
        }

        var provider = new FakeNotificationProvider();
        using var dispatchContext = _db.CreateContext();
        var service = new NotificationDispatchService(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())), provider, new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance), new NotificationEventRepository(dispatchContext), new DeviceTokenRepository(dispatchContext), new CustomerRepository(dispatchContext), new ProviderRepository(dispatchContext), new NoOpMetricsService(), NullLogger<NotificationDispatchService>.Instance);

        await service.DispatchAsync(customerId, NotificationEventType.Welcome, new NotificationRecipient("9876543210", null), new Dictionary<string, string>());

        using var readContext = _db.CreateContext();
        var logged = (await new NotificationEventRepository(readContext).ListByCustomerAsync(customerId)).Single();

        logged.Recipient.Should().NotBe("9876543210");
        logged.Recipient.Should().EndWith("3210");
    }

    [Fact]
    public async Task DispatchAsync_sends_one_push_per_registered_device()
    {
        Guid customerId;
        using (var context = _db.CreateContext())
        {
            customerId = SeedCustomer(context);
        }

        var pushProvider = new FakePushNotificationProvider();
        using var dispatchContext = _db.CreateContext();
        var service = new NotificationDispatchService(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())), new FakeNotificationProvider(), pushProvider, new NotificationEventRepository(dispatchContext), new DeviceTokenRepository(dispatchContext), new CustomerRepository(dispatchContext), new ProviderRepository(dispatchContext), new NoOpMetricsService(), NullLogger<NotificationDispatchService>.Instance);

        var outcomes = await service.DispatchAsync(
            customerId, NotificationEventType.Welcome, new NotificationRecipient(null, null, ["device-a", "device-b"]),
            new Dictionary<string, string> { ["CustomerName"] = "Asha" });

        outcomes.Should().HaveCount(2);
        outcomes.Should().OnlyContain(o => o.Channel == NotificationChannel.Push && o.Status == NotificationDeliveryStatus.Sent);
        pushProvider.SentPushes.Select(p => p.DeviceToken).Should().BeEquivalentTo(["device-a", "device-b"]);
        pushProvider.SentPushes.Should().OnlyContain(p => p.Title == "Welcome to Glavyx" && p.Body.Contains("Asha"));
    }

    [Fact]
    public async Task DispatchAsync_combines_sms_email_and_push_in_one_call()
    {
        Guid customerId;
        using (var context = _db.CreateContext())
        {
            customerId = SeedCustomer(context);
        }

        var smsEmailProvider = new FakeNotificationProvider();
        var pushProvider = new FakePushNotificationProvider();
        using var dispatchContext = _db.CreateContext();
        var service = new NotificationDispatchService(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())), smsEmailProvider, pushProvider, new NotificationEventRepository(dispatchContext), new DeviceTokenRepository(dispatchContext), new CustomerRepository(dispatchContext), new ProviderRepository(dispatchContext), new NoOpMetricsService(), NullLogger<NotificationDispatchService>.Instance);

        var outcomes = await service.DispatchAsync(
            customerId, NotificationEventType.Welcome, new NotificationRecipient("9876543210", "asha@example.com", ["device-a"]),
            new Dictionary<string, string> { ["CustomerName"] = "Asha" });

        outcomes.Should().HaveCount(3);
        outcomes.Select(o => o.Channel).Should().BeEquivalentTo([NotificationChannel.Sms, NotificationChannel.Email, NotificationChannel.Push]);
    }
}
