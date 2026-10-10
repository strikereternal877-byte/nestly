using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Abstractions.Time;
using Nestly.Application.Notifications;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Settings;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// Shared construction of the provider-queue model's collaborators, mirroring
/// <c>Nestly.Catalog.Tests.TestServices</c>'s own doc comment: a new cross-
/// cutting dependency is added in one place rather than in every suite's own
/// <c>CreateJobService</c>-style helper.
/// </summary>
internal static class TestServices
{
    /// <summary>UTC-pinned, same reasoning as Catalog.Tests' equivalent: fixtures build slot dates off <see cref="DateTime.UtcNow"/>.</summary>
    public static IBusinessClock Clock(TimeProvider? timeProvider = null) =>
        new BusinessClock(
            timeProvider ?? TimeProvider.System,
            Options.Create(new BusinessTimeOptions { TimeZoneId = "UTC" }));

    public static IProviderJobOccupancyService Occupancy(TimeProvider? timeProvider = null) =>
        new ProviderJobOccupancyService(Clock(timeProvider));

    public static IProviderActiveJobLimitService ActiveJobLimit(NestlyDbContext context) =>
        new ProviderActiveJobLimitService(context);

    /// <summary>Sandbox-backed (no network, no key) - never fires for a suite whose bookings share one address.</summary>
    public static IOverrunReassignmentService OverrunReassignment(NestlyDbContext context) =>
        new OverrunReassignmentService(
            context,
            new BookingRepository(context),
            new BookingProviderAssignmentRepository(context),
            new ProviderTravelFeasibilityService(
                context,
                new SandboxRouteEstimateProvider(Options.Create(new SandboxRouteEstimateOptions())),
                new SandboxRouteEstimateProvider(Options.Create(new SandboxRouteEstimateOptions())),
                Options.Create(new AutoAssignmentOptions()),
                NullLogger<ProviderTravelFeasibilityService>.Instance,
                Occupancy()),
            NullLogger<OverrunReassignmentService>.Instance);

    /// <summary>Mirrors <c>Nestly.Catalog.Tests.TestServices</c>'s own equivalent - see its doc comment.</summary>
    public static IAuditLogWriter AuditLogWriter(NestlyDbContext context) =>
        new AuditLogWriter(context, SystemAuditContextProvider.Instance);

    public static ISystemSettingsService SystemSettings(NestlyDbContext context) =>
        new SystemSettingsService(new SystemSettingRepository(context), AuditLogWriter(context), SystemAuditContextProvider.Instance);

    /// <summary>Real <see cref="IProviderNotificationPublisher"/> over the test database with the sandbox push provider - mirrors <c>Nestly.Catalog.Tests.TestServices</c>'s own equivalent.</summary>
    public static IProviderNotificationPublisher ProviderNotificationPublisher(NestlyDbContext context) =>
        new ProviderNotificationPublisher(
            new ProviderNotificationRepository(context),
            new DeviceTokenRepository(context),
            new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
            NullLogger<Nestly.Infrastructure.Services.ProviderNotificationPublisher>.Instance);

    private sealed class SystemAuditContextProvider : IAuditContextProvider
    {
        public static readonly SystemAuditContextProvider Instance = new();
        public AuditContext GetCurrent() => AuditContext.System;
    }
}
