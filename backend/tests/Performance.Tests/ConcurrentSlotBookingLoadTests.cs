using System.Diagnostics;
using FluentAssertions;
using Nestly.Application.Bookings;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Xunit.Abstractions;

namespace Nestly.Performance.Tests;

/// <summary>
/// The real-Postgres counterpart to <see cref="ConcurrentSlotBookingPerformanceTests"/>
/// (docs/PRODUCTION-READINESS.md §4.3, "concurrent slot booking under
/// contention is precisely the scenario where this platform's correctness
/// is most at risk and least observable... this needs a real load harness
/// against a running stack, and a recorded baseline to regress against").
///
/// <para>
/// <b>What the SQLite suite already proves, and what it cannot.</b> It
/// proves <c>SlotCapacityRepository</c>'s atomic conditional UPDATE is
/// logically correct: no more bookings than capacity, under real concurrent
/// connections, ever. What it cannot prove is anything about the actual
/// production engine - <c>ExecuteUpdateAsync</c>'s SQL translation is
/// provider-specific, Npgsql's connection pool and unique-violation surface
/// differ from Microsoft.Data.Sqlite's, and SQLite's single-writer model
/// (even in WAL mode) has no equivalent of Postgres row-level MVCC locking
/// under many truly parallel connections. This suite runs the identical
/// scenarios against <see cref="PostgresPerfTestDatabase"/> - see its doc
/// comment for how isolation works without a CREATEDB-privileged role - at
/// promotion-scale concurrency, and records real latency/throughput numbers
/// via <see cref="ITestOutputHelper"/> as the baseline the production-
/// readiness doc asked for. A future regression shows up here as a change in
/// these numbers or a correctness assertion failing, not as a silent
/// overbooked slot found by a customer.
/// </para>
///
/// <para>
/// Requires a reachable PostgreSQL server (see
/// <see cref="PostgresPerfTestDatabase"/> for the connection string and its
/// env var override) - deliberately not skipped when absent; see that
/// file's doc comment on why a load test skipping its own precondition is
/// worse than one that fails loudly.
/// </para>
/// </summary>
public sealed class ConcurrentSlotBookingLoadTests : IClassFixture<PostgresPerfTestDatabase>
{
    private readonly PostgresPerfTestDatabase _db;
    private readonly ITestOutputHelper _output;

    public ConcurrentSlotBookingLoadTests(PostgresPerfTestDatabase db, ITestOutputHelper output)
    {
        _db = db;
        _output = output;
    }

    /// <summary>Races <paramref name="fixture"/>'s customers concurrently against a real booking attempt each, timing every individual attempt (not just the batch) so percentiles are meaningful.</summary>
    private async Task<(Result<BookingDetailResponse>[] Results, double[] LatenciesMs, TimeSpan Wall)> RaceAsync(
        SlotBookingScenario.Fixture fixture)
    {
        var latencies = new double[fixture.Customers.Count];

        var tasks = fixture.Customers.Select(async (pair, i) =>
        {
            using var context = _db.CreateContext();
            var service = SlotBookingScenario.BuildBookingService(context);
            var sw = Stopwatch.StartNew();
            var result = await service.CreateAsync(pair.Customer.Id, SlotBookingScenario.RequestFor(fixture, pair.Address.Id));
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;
            return result;
        });

        var wall = Stopwatch.StartNew();
        var results = await Task.WhenAll(tasks);
        wall.Stop();

        return (results, latencies, wall.Elapsed);
    }

    private void ReportBaseline(string scenario, double[] latenciesMs, TimeSpan wall)
    {
        var sorted = latenciesMs.OrderBy(x => x).ToArray();
        double Percentile(double p) => sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];

        _output.WriteLine(
            $"[load-baseline] {scenario}: {sorted.Length} concurrent requests in {wall.TotalMilliseconds:F0}ms " +
            $"({sorted.Length / wall.TotalSeconds:F1} req/s) - " +
            $"latency ms: min={sorted[0]:F0} p50={Percentile(0.50):F0} p95={Percentile(0.95):F0} p99={Percentile(0.99):F0} max={sorted[^1]:F0}");
    }

    [Fact]
    public async Task Promotion_scale_race_respects_capacity_against_real_postgres()
    {
        // 70 concurrent customers for 30 seats: well past the SQLite suite's
        // 40-for-5, because a real engine can actually be asked to handle
        // "promotion-level traffic" (SRS 29.1-29.2) rather than SQLite's
        // single-writer queue making any concurrency figure moot beyond
        // proving correctness. Capped at 70, not higher, to stay under
        // PostgresPerfTestDatabase's 80-connection pool with headroom for
        // its own schema-admin connections and a standard Postgres server's
        // default max_connections=100 (see that file's MaxPoolSize comment) -
        // this needs to run unmodified against any standard Postgres
        // instance, local or CI, not just a specially provisioned one.
        const int capacity = 30;
        const int concurrentCustomers = 70;

        var fixture = SlotBookingScenario.SeedSlotWithCapacity(_db.CreateContext, capacity, concurrentCustomers);

        var (results, latencies, wall) = await RaceAsync(fixture);
        ReportBaseline($"{concurrentCustomers} racers, capacity {capacity}", latencies, wall);

        var succeeded = results.Where(r => r.IsSuccess).ToList();
        var failed = results.Where(r => r.IsFailure).ToList();

        succeeded.Should().HaveCount(capacity, "exactly capacity-many bookings must win the race, no more and no fewer, against the real database engine");
        failed.Should().HaveCount(concurrentCustomers - capacity);
        failed.Should().OnlyContain(r => r.Error.Code == "Booking.SlotCapacityReached");
        failed.Should().OnlyContain(r => r.Error.Type == ErrorType.Conflict);

        // The definitive check, same as the SQLite suite: what actually
        // landed in the database, not what the in-memory Results claim.
        using var readContext = _db.CreateContext();
        int persistedBookings = readContext.Bookings.Count(b => b.SlotWindowId == fixture.SlotWindowId && b.SlotDate == fixture.Date);
        persistedBookings.Should().Be(capacity, "Postgres must never persist more bookings for a slot+date than its configured capacity");

        var counter = readContext.Set<SlotBookingCounter>()
            .Single(c => c.SlotWindowId == fixture.SlotWindowId && c.SlotDate == fixture.Date);
        counter.BookedCount.Should().Be(capacity);
    }

    [Fact]
    public async Task Last_seat_race_has_exactly_one_winner_against_real_postgres()
    {
        // The sharpest edge, at higher concurrency than the SQLite suite's
        // 15: this is the path most likely to expose a real row-lock or
        // unique-constraint-race bug that a single-writer SQLite database
        // structurally cannot produce. See the pool-size comment on the
        // promotion-scale test above for why this stays at 70, not higher.
        const int concurrentCustomers = 70;

        var fixture = SlotBookingScenario.SeedSlotWithCapacity(_db.CreateContext, capacity: 1, concurrentCustomers);

        var (results, latencies, wall) = await RaceAsync(fixture);
        ReportBaseline($"{concurrentCustomers} racers, capacity 1", latencies, wall);

        results.Count(r => r.IsSuccess).Should().Be(1, "the last-seat race is the sharpest case: many customers, one winner, on the real engine");
        results.Count(r => r.IsFailure && r.Error.Code == "Booking.SlotCapacityReached").Should().Be(concurrentCustomers - 1);

        using var readContext = _db.CreateContext();
        int persistedBookings = readContext.Bookings.Count(b => b.SlotWindowId == fixture.SlotWindowId && b.SlotDate == fixture.Date);
        persistedBookings.Should().Be(1);
    }

    [Fact]
    public async Task Brand_new_slot_first_request_race_creates_counter_row_exactly_once()
    {
        // Targets SlotCapacityRepository's other race explicitly: many
        // requests hitting a slot+date with NO counter row yet, all racing
        // to be the one whose INSERT wins the unique index on
        // (SlotWindowId, SlotDate). The SQLite suite exercises this
        // incidentally (every SeedSlotWithCapacity scenario starts with no
        // counter row); this test isolates it at higher concurrency because
        // it is the one path where the loser takes a genuinely different
        // code path (catch DbUpdateException, detach, retry the conditional
        // UPDATE) rather than just losing the same UPDATE everyone else
        // contended for - and Npgsql's unique-violation surface (Postgres
        // error 23505) has never been exercised here before this suite. See
        // the pool-size comment on the promotion-scale test above for why
        // this stays at 70, not higher.
        const int concurrentCustomers = 70;

        var fixture = SlotBookingScenario.SeedSlotWithCapacity(_db.CreateContext, capacity: 20, concurrentCustomers);

        var (results, latencies, wall) = await RaceAsync(fixture);
        ReportBaseline($"{concurrentCustomers} racers onto a brand-new counter row, capacity 20", latencies, wall);

        results.Count(r => r.IsSuccess).Should().Be(20);

        using var readContext = _db.CreateContext();
        // Exactly one counter row must exist for this slot+date, regardless
        // of how many requests raced to create it - a duplicate row would
        // mean the unique-index race was lost silently rather than resolved
        // by the catch-and-retry path.
        readContext.Set<SlotBookingCounter>()
            .Count(c => c.SlotWindowId == fixture.SlotWindowId && c.SlotDate == fixture.Date)
            .Should().Be(1, "the unique index on (SlotWindowId, SlotDate) must let exactly one counter row exist, however many requests raced to create it");
    }
}
