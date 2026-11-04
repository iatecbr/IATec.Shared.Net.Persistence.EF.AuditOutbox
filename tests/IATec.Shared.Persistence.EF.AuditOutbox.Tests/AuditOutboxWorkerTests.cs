using System.Diagnostics;
using FluentAssertions;
using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;
using IATec.Shared.Persistence.EF.AuditOutbox.Entities;
using IATec.Shared.Persistence.EF.AuditOutbox.Persistence;
using IATec.Shared.Persistence.EF.AuditOutbox.Tests.Fakes;
using IATec.Shared.Persistence.EF.AuditOutbox.Worker;
using IATec.Shared.HttpClient.Dto;
using IATec.Shared.HttpClient.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Tests;

/// <summary>
/// Tests for <see cref="AuditOutboxWorker{TContext}"/> driven through its real
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> poll loop with a fake
/// <see cref="IServiceClient"/>. Covers success deletion (Req 7.5), failure marking including the
/// open-circuit case (Req 7.6, 8.2/8.3), <see cref="AuditOutboxOptions.BatchSize"/> (Req 7.2/8.1)
/// and <see cref="AuditOutboxOptions.RetryLimit"/> exclusion (Req 8.4).
/// </summary>
/// <remarks>
/// <para>
/// Determinism strategy: the worker's real <c>ExecuteAsync</c> loop runs with a very small
/// <see cref="AuditOutboxOptions.PollInterval"/>. The worker is started, the test poll-waits (with a
/// bounded timeout) until the database reaches the expected terminal state, then the worker is
/// stopped. For the single-cycle <see cref="AuditOutboxOptions.BatchSize"/> assertion the fake is
/// gated: it signals a <see cref="TaskCompletionSource"/> once the expected number of deliveries has
/// happened, so the test can stop the worker right after the first claim and assert the remaining
/// rows are still unclaimed. No unbounded waits are used.
/// </para>
/// <para>
/// Thread-safety: a single kept-open SQLite connection carries the shared in-memory database, but
/// each actor gets its OWN <see cref="DbContext"/> over that connection — the worker resolves a
/// fresh context per scope while the test reads through a separate context. This avoids sharing one
/// (non-thread-safe) <see cref="DbContext"/> between the worker thread and the test thread.
/// </para>
/// </remarks>
public sealed class AuditOutboxWorkerTests : IDisposable
{
    private static readonly TimeSpan ShortPoll = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly string _connectionString;
    private readonly SqliteConnection _keepAlive;

    public AuditOutboxWorkerTests()
    {
        // A named, shared-cache in-memory database. Unlike a bare ":memory:" connection (which is
        // private to that single connection and is NOT safe to share across the worker thread and
        // the test thread), a shared-cache named database lets EACH context open its OWN connection
        // to the SAME data. That gives the worker and the test independent, thread-safe connections
        // while observing the same rows — eliminating the cross-thread races on a single connection.
        _connectionString = $"DataSource=audit-worker-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        // One kept-open connection keeps the shared in-memory database alive for the whole test:
        // the database is destroyed once the last connection to it closes.
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        using var schema = CreateContext();
        schema.Database.EnsureCreated();
    }

    public void Dispose() => _keepAlive.Dispose();

    private WorkerTestContext CreateContext() => WorkerTestContext.Over(_connectionString);

    private static AuditOutboxOptions Options(int batchSize = 100, int retryLimit = 3) => new()
    {
        ContainerKey = "container",
        LogServiceUrl = "https://audit.example.com",
        PollInterval = ShortPoll,
        BatchSize = batchSize,
        RetryLimit = retryLimit,
    };

    private static AuditLogEntry PendingEntry(string dedupKey) => new()
    {
        PayloadJson = $"{{\"k\":\"{dedupKey}\"}}",
        DedupKey = dedupKey,
        CreatedAt = DateTime.UtcNow,
        Status = AuditLogStatus.Pending,
        RetryCount = 0,
    };

    private void Seed(params AuditLogEntry[] entries)
    {
        using var context = CreateContext();
        context.AuditLogEntries.AddRange(entries);
        context.SaveChanges();
    }

    /// <summary>
    /// Builds a <see cref="ServiceProvider"/> whose scopes resolve a fresh <see cref="WorkerTestContext"/>
    /// over the shared database and the supplied fake service-client factory.
    /// </summary>
    private ServiceProvider BuildProvider(IServiceClient client)
    {
        var services = new ServiceCollection();

        // The worker creates a scope per cycle and resolves TContext + IServiceClientFactory.
        services.AddScoped(_ => CreateContext());
        services.AddSingleton<IServiceClientFactory>(new FakeServiceClientFactory(client));

        return services.BuildServiceProvider();
    }

    /// <summary>Polls <paramref name="condition"/> until true or the bounded timeout elapses.</summary>
    private async Task<bool> WaitUntilAsync(Func<WorkerTestContext, bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < WaitTimeout)
        {
            if (Evaluate())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return Evaluate();

        bool Evaluate()
        {
            using var context = CreateContext();
            return condition(context);
        }
    }

    [Fact]
    public async Task Cycle_WhenAllDeliveriesSucceed_DeletesEntriesAndCallsServiceOncePerEntry()
    {
        Seed(PendingEntry("A"), PendingEntry("B"));

        var client = FakeServiceClient.Returning(BaseResponseDtoFactory.Success());
        await using var provider = BuildProvider(client);

        var worker = new AuditOutboxWorker<WorkerTestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options());

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var drained = await WaitUntilAsync(ctx => !ctx.AuditLogEntries.AsNoTracking().Any());

        await worker.StopAsync(CancellationToken.None);

        drained.Should().BeTrue("both successfully delivered entries must be deleted (Req 7.5)");

        using var reader = CreateContext();
        reader.AuditLogEntries.AsNoTracking().Count().Should().Be(0);
        client.PostCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Cycle_WhenServiceReturnsFailure_MarksEntryFailedIncrementsRetryAndStoresInfo()
    {
        Seed(PendingEntry("F1"));

        var client = FakeServiceClient.Returning(BaseResponseDtoFactory.Failure(503, "service unavailable"));
        await using var provider = BuildProvider(client);

        // RetryLimit = 1 so that once the single failure increments RetryCount to 1 the entry is
        // excluded from subsequent cycles (RetryCount < RetryLimit is false). This freezes the row
        // at Failed/RetryCount=1 and makes the post-stop assertion deterministic — no later cycle
        // can reclaim it and flip it back to Processing.
        var worker = new AuditOutboxWorker<WorkerTestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options(retryLimit: 1));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var marked = await WaitUntilAsync(ctx =>
            ctx.AuditLogEntries.AsNoTracking().Any(e => e.Status == AuditLogStatus.Failed));

        await worker.StopAsync(CancellationToken.None);

        marked.Should().BeTrue("a failed delivery must leave the row Failed (Req 7.6)");

        using var reader = CreateContext();
        var row = reader.AuditLogEntries.AsNoTracking().Single();
        row.Status.Should().Be(AuditLogStatus.Failed);
        row.RetryCount.Should().Be(1);
        row.AdditionalInfo.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cycle_WhenServiceThrowsOpenCircuit_MarksEntryFailedIncrementsRetryAndStoresInfo()
    {
        Seed(PendingEntry("C1"));

        // A thrown exception models an open circuit / timeout / transport error (Req 8.2/8.3).
        var client = FakeServiceClient.Throwing(new InvalidOperationException("circuit open"));
        await using var provider = BuildProvider(client);

        // RetryLimit = 1 freezes the row at Failed/RetryCount=1 after the single failure so no later
        // cycle reclaims it (see the failure-response test for the rationale).
        var worker = new AuditOutboxWorker<WorkerTestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options(retryLimit: 1));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var marked = await WaitUntilAsync(ctx =>
            ctx.AuditLogEntries.AsNoTracking().Any(e => e.Status == AuditLogStatus.Failed));

        await worker.StopAsync(CancellationToken.None);

        marked.Should().BeTrue("an open circuit is treated as a delivery failure (Req 8.2/8.3)");

        using var reader = CreateContext();
        var row = reader.AuditLogEntries.AsNoTracking().Single();
        row.Status.Should().Be(AuditLogStatus.Failed);
        row.RetryCount.Should().Be(1);
        row.AdditionalInfo.Should().Contain("circuit open");
    }

    [Fact]
    public async Task Cycle_WhenBatchSizeIsTwo_ClaimsAtMostBatchSizeEntriesPerCycle()
    {
        Seed(
            PendingEntry("B1"),
            PendingEntry("B2"),
            PendingEntry("B3"),
            PendingEntry("B4"),
            PendingEntry("B5"));

        const int batchSize = 2;

        // Gated fake: on each delivery it records how many rows are still Pending (unclaimed) in the
        // database at that instant, then blocks the worker until the test releases it. Blocking the
        // first cycle in-flight guarantees no second cycle can start and distort the counts, so the
        // single-cycle BatchSize semantics are observed deterministically (Req 7.2/8.1).
        var client = new GatedFakeServiceClient(
            batchSize,
            () =>
            {
                using var ctx = CreateContext();
                return ctx.AuditLogEntries.AsNoTracking().Count(e => e.Status == AuditLogStatus.Pending);
            });

        await using var provider = BuildProvider(client);

        var worker = new AuditOutboxWorker<WorkerTestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options(batchSize: batchSize));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var reached = await Task.WhenAny(client.ReachedBatch, Task.Delay(WaitTimeout));
        reached.Should().Be(client.ReachedBatch, "the gated fake must receive BatchSize deliveries in one cycle");

        // Release the blocked worker and stop it before any further cycle can run.
        client.Release();
        await worker.StopAsync(CancellationToken.None);

        // During the single claim of this cycle only BatchSize entries were marked Processing, so at
        // the moment of each delivery the other 3 rows were still Pending (never claimed).
        client.PostCallCount.Should().Be(batchSize);
        client.MinPendingObserved.Should().Be(3, "only BatchSize entries may be claimed per cycle");
    }

    [Fact]
    public async Task Cycle_ExcludesFailedEntriesAtRetryLimitAndProcessesPendingOnes()
    {
        const int retryLimit = 3;

        var exhausted = PendingEntry("EXHAUSTED");
        exhausted.Status = AuditLogStatus.Failed;
        exhausted.RetryCount = retryLimit; // at the limit -> must be excluded (Req 8.4)
        exhausted.AdditionalInfo = "previous failure";

        Seed(exhausted, PendingEntry("PENDING"));

        var client = FakeServiceClient.Returning(BaseResponseDtoFactory.Success());
        await using var provider = BuildProvider(client);

        var worker = new AuditOutboxWorker<WorkerTestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options(retryLimit: retryLimit));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Wait until the pending entry has been delivered (deleted).
        var processed = await WaitUntilAsync(ctx =>
            !ctx.AuditLogEntries.AsNoTracking().Any(e => e.DedupKey == "PENDING"));

        await worker.StopAsync(CancellationToken.None);

        processed.Should().BeTrue("the Pending entry must be processed and deleted (Req 7.5)");

        // The exhausted entry was never touched: still present, still Failed, RetryCount unchanged,
        // and the fake was only ever called for the single eligible entry.
        using var reader = CreateContext();
        var remaining = reader.AuditLogEntries.AsNoTracking().ToList();
        remaining.Should().ContainSingle();

        var untouched = remaining.Single();
        untouched.DedupKey.Should().Be("EXHAUSTED");
        untouched.Status.Should().Be(AuditLogStatus.Failed);
        untouched.RetryCount.Should().Be(retryLimit);
        client.PostCallCount.Should().Be(1, "the retry-exhausted entry must be excluded from the batch (Req 8.4)");
    }

    [Fact]
    public async Task Cycle_RescuesEntryStuckInProcessingPastTimeout_AndDelivers()
    {
        // Simulates a crash/restart: the entry was claimed (Processing) but never delivered, and its
        // claim timestamp is older than ProcessingTimeout. A later cycle must rescue and deliver it.
        var stuck = PendingEntry("STUCK");
        stuck.Status = AuditLogStatus.Processing;
        stuck.ProcessingSince = DateTime.UtcNow.AddMinutes(-10);
        Seed(stuck);

        var client = FakeServiceClient.Returning(BaseResponseDtoFactory.Success());
        await using var provider = BuildProvider(client);

        var options = Options();
        options.ProcessingTimeout = TimeSpan.FromMinutes(1);

        var worker = new AuditOutboxWorker<WorkerTestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var delivered = await WaitUntilAsync(ctx => !ctx.AuditLogEntries.AsNoTracking().Any());

        await worker.StopAsync(CancellationToken.None);

        delivered.Should().BeTrue("a Processing entry older than ProcessingTimeout must be rescued and delivered");
        client.PostCallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Cycle_DoesNotStealEntryRecentlyClaimedByAnotherReplica()
    {
        // A Processing entry claimed just now (within ProcessingTimeout) is assumed to be in-flight
        // on another active replica and must NOT be picked up.
        var inFlight = PendingEntry("IN-FLIGHT");
        inFlight.Status = AuditLogStatus.Processing;
        inFlight.ProcessingSince = DateTime.UtcNow;
        Seed(inFlight);

        var client = FakeServiceClient.Returning(BaseResponseDtoFactory.Success());
        await using var provider = BuildProvider(client);

        var options = Options();
        options.ProcessingTimeout = TimeSpan.FromMinutes(5);

        var worker = new AuditOutboxWorker<WorkerTestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Give the worker a few poll cycles to prove it leaves the entry alone.
        await Task.Delay(200);

        await worker.StopAsync(CancellationToken.None);

        using var reader = CreateContext();
        var row = reader.AuditLogEntries.AsNoTracking().Single();
        row.DedupKey.Should().Be("IN-FLIGHT");
        row.Status.Should().Be(AuditLogStatus.Processing, "a freshly-claimed entry must not be stolen");
        client.PostCallCount.Should().Be(0, "no delivery should happen for an in-flight entry");
    }
    /// <summary>
    /// Minimal relational context mapping only <see cref="AuditLogEntry"/> (via the production
    /// <see cref="AuditLogEntryConfiguration"/>). Each instance opens its own connection to the
    /// shared-cache in-memory database identified by the connection string, so many short-lived
    /// contexts across threads can share one database safely.
    /// </summary>
    private sealed class WorkerTestContext : DbContext
    {
        private WorkerTestContext(DbContextOptions<WorkerTestContext> options) : base(options)
        {
        }

        public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

        public static WorkerTestContext Over(string connectionString)
        {
            var options = new DbContextOptionsBuilder<WorkerTestContext>()
                .UseSqlite(connectionString)
                .Options;

            return new WorkerTestContext(options);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new AuditLogEntryConfiguration());
        }
    }

    /// <summary>
    /// Fake <see cref="IServiceClient"/> that always returns success. On every delivery it samples a
    /// caller-supplied observation (here, the number of still-Pending rows) and, once the expected
    /// number of deliveries has happened, signals <see cref="ReachedBatch"/> and blocks until the
    /// test calls <see cref="Release"/>. Blocking the first cycle in-flight makes the single-cycle
    /// <see cref="AuditOutboxOptions.BatchSize"/> assertion deterministic.
    /// </summary>
    private sealed class GatedFakeServiceClient : IServiceClient
    {
        private readonly int _signalAfter;
        private readonly Func<int> _observePending;
        private readonly TaskCompletionSource _reachedBatch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _gate = new(initialState: false);
        private readonly object _lock = new();

        public GatedFakeServiceClient(int signalAfter, Func<int> observePending)
        {
            _signalAfter = signalAfter;
            _observePending = observePending;
            MinPendingObserved = int.MaxValue;
        }

        public int PostCallCount { get; private set; }

        /// <summary>Smallest Pending-row count observed across all deliveries.</summary>
        public int MinPendingObserved { get; private set; }

        /// <summary>Completes once <see cref="_signalAfter"/> deliveries have been received.</summary>
        public Task ReachedBatch => _reachedBatch.Task;

        /// <summary>Unblocks any delivery currently waiting on the gate.</summary>
        public void Release() => _gate.Set();

        public Task<BaseResponseDto> PostAsync(string url, HttpContent content)
        {
            bool shouldBlock;

            lock (_lock)
            {
                PostCallCount++;
                var pending = _observePending();
                MinPendingObserved = Math.Min(MinPendingObserved, pending);

                shouldBlock = PostCallCount >= _signalAfter;
                if (shouldBlock)
                {
                    _reachedBatch.TrySetResult();
                }
            }

            if (shouldBlock)
            {
                // Hold the worker inside this cycle until the test releases it, preventing a second
                // cycle from starting and claiming more rows.
                _gate.Wait();
            }

            return Task.FromResult(BaseResponseDtoFactory.Success());
        }

        public Task<ResponseDto<T>> PostAsync<T>(string url, HttpContent content) where T : class =>
            throw new NotSupportedException();

        public Task<BaseResponseDto> PutAsync(string url, HttpContent content) =>
            throw new NotSupportedException();

        public Task<ResponseDto<T>> PutAsync<T>(string url, HttpContent content) where T : class =>
            throw new NotSupportedException();

        public Task<BaseResponseDto> DeleteAsync(string url) =>
            throw new NotSupportedException();

        public Task<ResponseDto<T>> DeleteAsync<T>(string url) where T : class =>
            throw new NotSupportedException();

        public Task<ResponseDto<T>> GetAsync<T>(string url) where T : class =>
            throw new NotSupportedException();

        public Task<ResponseDto<string>> GetStringAsync(string url) =>
            throw new NotSupportedException();
    }
}
