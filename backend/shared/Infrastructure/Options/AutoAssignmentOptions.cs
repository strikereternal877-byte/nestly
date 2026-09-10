using System.ComponentModel.DataAnnotations;

namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "AutoAssignment" configuration section
/// (PROVIDER.md OPEN DECISIONS - AUTOMATIC ASSIGNMENT, tasks 247/248). No
/// admin UI to manage this yet, same not-yet-adminable-policy-knob reasoning
/// as <see cref="CommissionOptions"/>.
/// </summary>
public class AutoAssignmentOptions
{
    public const string SectionName = "AutoAssignment";

    /// <summary>
    /// Task 248's kill switch: when false, <c>ProviderAutoAssignmentHandler</c>
    /// takes no action at all on any booking - falls back to today's fully
    /// manual admin-assignment flow, with no other behaviour change. Default
    /// true (same as <see cref="Nestly.Infrastructure.Options.BackgroundJobOptions.ServerEnabled"/>'s
    /// convention: on by default, an explicit override to turn off) - lets
    /// ops disable a misbehaving first-release auto-dispatch engine in
    /// production without a deploy, purely via configuration.
    /// </summary>
    /// <remarks>
    /// Task 363: <c>ProviderAutoAssignmentHandler</c> is an in-process
    /// notification handler, so it runs in whichever API process raised the
    /// transition to <see cref="Nestly.Domain.BookingStatus.AwaitingFulfilment"/>
    /// - consumer-api on a reschedule that needs reassignment, provider-api on
    /// an assignment rejection, admin-api on the promotion sweep. This switch
    /// is therefore materialised in all three APIs' <c>appsettings.json</c>:
    /// turning it off in one process only would leave the matcher live in the
    /// other two. The <see cref="PromotionEnabled"/> group is the opposite
    /// case and is admin-api-only for the same kind of reason - see it.
    /// <c>AutoAssignmentConfigurationReachTests</c> pins both.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Task 247: how many times <c>ProviderAutoAssignmentHandler</c> retries
    /// the next-best candidate after a rejection before leaving the booking
    /// for the manual admin queue. Decision 6: 3 - past a handful of
    /// declines the pattern is more likely a genuinely hard-to-place booking
    /// than one more retry fixing it, and the number itself has no
    /// production data behind it yet, hence configurable rather than a
    /// hardcoded constant.
    /// </summary>
    [Range(0, 20)]
    public int RetryAttempts { get; set; } = 3;

    /// <summary>
    /// Task 267's kill switch: when false, <c>ProviderMatchingService</c>
    /// ranks candidates purely by great-circle distance, exactly as it did
    /// before real travel time became an input - no route call is issued and
    /// <c>ProviderMatchCandidate.TravelDurationSeconds</c> is null on every
    /// candidate. Default true, same convention as <see cref="Enabled"/> and
    /// <see cref="GoogleMapsOptions.Enabled"/>: on by default, an explicit
    /// override to turn off, so a routing outage or a billing incident is one
    /// configuration change rather than a deploy.
    /// </summary>
    /// <remarks>
    /// Turning it off is not the only way to stop paying for routing:
    /// <see cref="GoogleMapsOptions.Enabled"/> already forces the sandbox
    /// estimator, and a sandbox-only response is deliberately ignored for
    /// ranking (see <c>ProviderMatchingService</c>). This switch exists as
    /// well because it also removes the call itself - the cheapest possible
    /// posture - and because a kill switch for a feature should live beside
    /// the feature's own options.
    /// </remarks>
    public bool RouteRankingEnabled { get; set; } = true;

    /// <summary>
    /// How many of the straight-line-nearest candidates are priced with a real
    /// route call. This is the cost cap: candidate discovery filters by skill
    /// and service area, but a popular city can still leave dozens of eligible
    /// providers, and every extra destination is a billed Routes API element.
    /// Ten keeps one booking to a single request (well under
    /// <see cref="GoogleMapsOptions.MaxDestinationsPerCall"/>) while still
    /// giving road travel time enough candidates to reorder - past the ten
    /// nearest by air, a candidate winning on road time is vanishingly
    /// unlikely.
    /// </summary>
    /// <remarks>
    /// A cap, never a filter: candidates beyond it are still returned and
    /// still assignable, just ordered by great-circle distance behind the
    /// priced ones (which are, by construction, all nearer by air).
    /// </remarks>
    [Range(1, 50)]
    public int MaxRouteCandidates { get; set; } = 10;

    /// <summary>
    /// Great-circle radius, in kilometres, beyond which a candidate is not
    /// worth a route call. Twenty-five covers a metro's realistic service
    /// radius; a provider farther away than that loses on travel time however
    /// the road runs, so paying to measure it exactly buys nothing.
    /// </summary>
    /// <remarks>
    /// Also a cost cap, not an eligibility rule: a candidate outside the
    /// radius is still returned and still assignable - if it is the only
    /// candidate it is still the one that gets assigned, with no route call
    /// issued at all. Excluding it here would silently narrow the eligible
    /// pool that skill/service-area/availability already define, which is a
    /// different (product) decision than "don't spend money measuring it".
    /// </remarks>
    [Range(0.1, 1000.0)]
    public decimal RouteRankingRadiusKm { get; set; } = 25m;

    /// <summary>
    /// Task 289's kill switch: when false, <c>ProviderTravelFeasibilityService</c>
    /// finds nothing and the eligibility gate is exactly what it was before
    /// travel time between adjacent jobs became an input - a provider
    /// finishing across the city at 11:00 is once again eligible for an 11:00
    /// job. No route call is issued. Default true, same convention as
    /// <see cref="Enabled"/> and <see cref="RouteRankingEnabled"/>: on by
    /// default, an explicit override to turn off, so a routing outage or a
    /// pathological schedule is one configuration change rather than a deploy.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="RouteRankingEnabled"/> on purpose: that one
    /// only changes the <i>order</i> candidates are tried in, this one changes
    /// who is eligible at all. Turning off ranking to stop spending money
    /// should not quietly re-open the door to physically impossible
    /// assignments, so neither switch implies the other.
    /// </remarks>
    public bool TravelBufferEnabled { get; set; } = true;

    /// <summary>
    /// Fixed allowance added on top of the measured drive between two adjacent
    /// jobs: parking, finding the door, and handover at both ends. Twenty
    /// minutes is the agreed default (provider-queue model) with no production
    /// data behind it yet - hence configurable rather than a constant - and
    /// errs towards the provider, since the cost of being wrong is a late
    /// arrival at a customer's home.
    /// </summary>
    /// <remarks>
    /// Added only when there is a drive to buffer. A zero-length leg (the next
    /// job at the same address - a second flat in one building, a follow-up for
    /// the same customer) requires no gap at all, which is what keeps task
    /// 288's deliberately-legal back-to-back case legal. Setting this to 0 is
    /// therefore not the same as turning the check off: pure travel time is
    /// still enforced.
    /// </remarks>
    [Range(0, 240)]
    public int TravelHandoverBufferMinutes { get; set; } = 20;

    /// <summary>
    /// The cost cap on task 289: how many route lookups one eligibility pass
    /// may bill for. A "lookup" is one destination leg - one billed Routes API
    /// element - and a single candidate needs at most two (the job before and
    /// the job after), batched into one request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Twenty is <see cref="MaxRouteCandidates"/> candidates x both legs: an
    /// engine that walks the whole ranked list can still price every one of
    /// them, and cannot fan out past that however many candidates a popular
    /// city returns. In practice a pass costs far less - the vast majority of
    /// candidates have no other job that day at all, and those cost nothing.
    /// </para>
    /// <para>
    /// Past the cap the check does <b>not</b> stop running: it switches to the
    /// local sandbox estimate, which needs no network and no billing account.
    /// The cap bounds spending, not the invariant - a candidate is never
    /// allowed through merely because the budget ran out. Setting it to 0 is
    /// a legitimate posture: enforce travel feasibility on the free
    /// approximation and never call the maps provider at all.
    /// </para>
    /// </remarks>
    [Range(0, 200)]
    public int MaxTravelRouteLookups { get; set; } = 20;

    /// <summary>
    /// Task 333's kill switch: when false, <c>BookingFulfilmentPromotionJob</c>
    /// promotes nothing and a <see cref="Nestly.Domain.BookingStatus.Confirmed"/>
    /// booking only reaches <see cref="Nestly.Domain.BookingStatus.AwaitingFulfilment"/>
    /// the way it did before this job existed - a reschedule, an assignment
    /// rejection, or an admin acting by hand. Default true, same convention as
    /// <see cref="Enabled"/>: on by default, an explicit override to turn off,
    /// so a dispatch incident is one configuration change rather than a deploy.
    /// </summary>
    /// <remarks>
    /// Deliberately a <b>separate</b> switch from <see cref="Enabled"/>, and
    /// neither implies the other. <see cref="Enabled"/> governs who gets
    /// picked; this one governs when a booking becomes pickable at all. With
    /// this on and <see cref="Enabled"/> off, bookings still surface in the
    /// admin's manual assignment queue as their slot approaches - which is the
    /// pre-automation flow, not a broken one - and turning this off must not
    /// be the only way to stop the matcher spending money on route lookups.
    /// </para>
    /// <para>
    /// Unlike <see cref="Enabled"/>, this and the three other
    /// <c>Promotion*</c> settings are materialised in admin-api's
    /// <c>appsettings.json</c> only: <c>BookingFulfilmentPromotionJob</c> is
    /// scheduled from admin-api's <c>Program.cs</c> alone, so writing them
    /// into consumer-api or provider-api would hand ops a knob that silently
    /// does nothing (task 361/363).
    /// </para>
    /// </remarks>
    public bool PromotionEnabled { get; set; } = true;

    /// <summary>
    /// How long before its slot starts a <see cref="Nestly.Domain.BookingStatus.Confirmed"/>
    /// booking is promoted to <see cref="Nestly.Domain.BookingStatus.AwaitingFulfilment"/>,
    /// which is what puts it in front of the matching engine (decision 4,
    /// PROVIDER.md). Twenty-four hours: late enough that the provider
    /// availability, capacity and travel feasibility the engine reads are the
    /// ones that will actually hold on the day, and early enough that a
    /// booking the engine cannot place still has a full day in the manual
    /// admin queue before the customer is expecting someone at the door.
    /// </summary>
    /// <remarks>
    /// This is a window, not a schedule. It interacts with the job's cron
    /// cadence (see <c>BookingFulfilmentPromotionJobScheduleExtensions</c>):
    /// a booking confirmed <i>inside</i> the window - every same-day booking
    /// is - is promoted on the next pass rather than after this lead time, so
    /// the cadence, not this value, is what bounds the delay for those. Any
    /// value here is safe with a cadence finer than it; a cadence coarser than
    /// this window would silently skip bookings, which is why the two are
    /// documented together.
    /// </remarks>
    [Range(1, 720)]
    public int PromotionLeadTimeHours { get; set; } = 24;

    /// <summary>
    /// Task 358, the other end of <see cref="PromotionLeadTimeHours"/>: how far
    /// into the past a slot may already have started and still be worth
    /// promoting. A booking whose slot began within this many hours is still a
    /// live dispatch problem - somebody can be sent, today, to a customer who is
    /// still expecting them - and is promoted exactly as before. Anything older
    /// is skipped. Twenty-four hours, mirroring
    /// <see cref="PromotionLeadTimeHours"/>: one full day either side of now is
    /// a single span an operator can hold in their head during an incident, and
    /// a slot that started more than a day ago is a support and refund question,
    /// not a dispatch one - no provider can be sent to yesterday.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because the sweep was previously unbounded below, so the first
    /// pass in any environment would promote the platform's entire history of
    /// past-dated <see cref="Nestly.Domain.BookingStatus.Confirmed"/> rows into
    /// the admin's manual queue at once - burying the handful that are genuinely
    /// actionable under a backlog nobody can dispatch. Bounding it changes what
    /// the queue means, not what the sweep knows: a skipped booking is left
    /// exactly as it was found, <c>Confirmed</c>, and is still reachable by
    /// admin search and reporting.
    /// </para>
    /// <para>
    /// 0 is a meaningful setting, unlike on <see cref="PromotionLeadTimeHours"/>
    /// (hence the range starting there rather than at 1): it means "promote only
    /// bookings whose slot has not started yet", the strictest posture. The
    /// ceiling matches <see cref="PromotionLeadTimeHours"/>'s at 720 hours -
    /// past a month the setting is doing nothing this bound was added for.
    /// </para>
    /// </remarks>
    [Range(0, 720)]
    public int PromotionMaxSlotAgeHours { get; set; } = 24;

    /// <summary>
    /// How many due bookings one promotion pass loads at a time. The sweep
    /// pages until it stops making progress rather than loading every due
    /// booking into memory at once, so this bounds a pass's working set, not
    /// how much it can get through.
    /// </summary>
    [Range(1, 1000)]
    public int PromotionBatchSize { get; set; } = 100;

    /// <summary>
    /// How long a system-assigned provider has to accept or reject before the
    /// assignment-response-expiry sweep treats their silence as a decline and
    /// tries the next candidate. Already externally configurable (per-API
    /// appsettings.json/environment) - only the default has changed.
    ///
    /// Row 38, docs/OPEN-FIXES-FEATURES.csv: the previous 15-minute default
    /// was "extremely short" for a working professional who may be mid-job,
    /// driving, or simply not looking at their phone the instant an offer
    /// lands - and, before task d9f557c's provider-web session-refresh fix,
    /// a lapsed session could make the accept attempt itself fail silently
    /// while the window kept counting down. Raised to thirty minutes: long
    /// enough for a working professional to notice and act on an offer
    /// between jobs, still short enough that a customer whose provider never
    /// responds is not left waiting for a manual admin to notice. Only
    /// system assignments get a deadline from this value - an admin's own
    /// manual assignment still sets
    /// <see cref="Nestly.Domain.BookingProviderAssignment.ResponseDeadline"/>
    /// explicitly via <c>AssignProviderRequest.ResponseDeadline</c>, or not
    /// at all - and it is also what <c>IBookingProviderAssignmentService.ExtendResponseDeadlineAsync</c>
    /// extends an outstanding deadline by when a provider's own accept
    /// attempt fails for a reason that was not their fault.
    /// </summary>
    [Range(1, 1440)]
    public int ResponseWindowMinutes { get; set; } = 30;
}
