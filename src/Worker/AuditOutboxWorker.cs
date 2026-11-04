using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;
using IATec.Shared.Persistence.EF.AuditOutbox.Entities;
using IATec.Shared.HttpClient.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Worker;

/// <summary>
/// Background service that periodically drains pending <see cref="AuditLogEntry"/> rows from the
/// client's <see cref="DbContext"/> to the remote audit service.
/// </summary>
/// <typeparam name="TContext">
/// The client's concrete <see cref="DbContext"/> type. The outbox package does not know this type
/// at compile time, so the worker is generic over it and resolves a scoped instance from DI on each
/// cycle. The resolved context must have the <see cref="AuditLogEntry"/> entity in its model (set up
/// by the outbox model customizer), accessed through <c>Set&lt;AuditLogEntry&gt;()</c>.
/// </typeparam>
/// <remarks>
/// <para>
/// Each cycle (Requirements 7.1, 7.2, 7.3, 8.1): wait <see cref="AuditOutboxOptions.PollInterval"/>,
/// create a DI scope, select up to <see cref="AuditOutboxOptions.BatchSize"/> entries that are
/// <see cref="AuditLogStatus.Pending"/>, <see cref="AuditLogStatus.Failed"/> below
/// <see cref="AuditOutboxOptions.RetryLimit"/>, or stuck in <see cref="AuditLogStatus.Processing"/>
/// past <see cref="AuditOutboxOptions.ProcessingTimeout"/> (rescued after a worker/host crash),
/// stamp them <see cref="AuditLogStatus.Processing"/> with the claim time and persist the claim
/// (anti double-pick across replicas — Requirement 7.7 / 8.1), then deliver each entry through
/// <see cref="AuditLogSender"/> and flush the resulting removals/updates.
/// </para>
/// <para>
/// Resilience (Requirements 7.x / 8.2 / 8.3): the whole cycle body is wrapped in a try/catch so a
/// transient failure (database hiccup, open circuit, etc.) is logged and the host keeps running;
/// an exception never escapes <see cref="ExecuteAsync"/>. Cancellation through the
/// <c>stoppingToken</c> during shutdown breaks the loop cleanly and is not logged as an error.
/// Entries at or over <see cref="AuditOutboxOptions.RetryLimit"/> are excluded by the query and are
/// never reprocessed (Requirement 8.4).
/// </para>
/// </remarks>
public sealed class AuditOutboxWorker<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuditOutboxOptions _options;
    private readonly ILogger<AuditOutboxWorker<TContext>>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditOutboxWorker{TContext}"/> class.
    /// </summary>
    /// <param name="scopeFactory">
    /// Factory used to create a DI scope per poll cycle. The worker is a singleton while the
    /// <typeparamref name="TContext"/> and <see cref="AuditLogSender"/> are scoped, so a fresh scope
    /// is required each cycle to obtain a short-lived, isolated context.
    /// </param>
    /// <param name="options">The audit outbox configuration (poll interval, batch size, retry limit).</param>
    /// <param name="logger">Optional logger used to record per-cycle failures. May be <c>null</c>.</param>
    public AuditOutboxWorker(
        IServiceScopeFactory scopeFactory,
        AuditOutboxOptions options,
        ILogger<AuditOutboxWorker<TContext>>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <summary>
    /// Runs the poll loop until the host signals shutdown through <paramref name="stoppingToken"/>.
    /// </summary>
    /// <param name="stoppingToken">Token signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown requested during the wait: exit the loop without logging.
                break;
            }

            try
            {
                await ProcessCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown requested mid-cycle: stop cleanly, this is not an error.
                break;
            }
            catch (Exception ex)
            {
                // Per-cycle resilience: never let a failure crash the host. Log and retry next cycle.
                _logger?.LogError(ex, "Audit outbox worker cycle failed; will retry on the next poll interval.");
            }
        }
    }

    /// <summary>
    /// Executes a single poll cycle: claim a batch, deliver it, and flush the results.
    /// </summary>
    /// <param name="stoppingToken">Token signalled when the host is shutting down.</param>
    private async Task ProcessCycleAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;

        // Resolve the client's concrete DbContext for this scope. Task 11 may register the sender
        // in DI; until then build it directly from the resolved context to avoid a hard coupling.
        var dbContext = provider.GetRequiredService<TContext>();
        var serviceClientFactory = provider.GetRequiredService<IServiceClientFactory>();
        var sender = new AuditLogSender(dbContext, serviceClientFactory, _options);

        var set = dbContext.Set<AuditLogEntry>();

        // Rescue threshold: a Processing entry whose claim is older than this was abandoned by a
        // worker/host that crashed mid-delivery, so it becomes eligible again. Entries claimed more
        // recently than this are assumed to be in-flight on another active replica and are left
        // alone (anti double-pick — Requirement 7.7 / 8.1).
        var staleBefore = DateTime.UtcNow - _options.ProcessingTimeout;

        // Select the drainable batch: Pending; Failed still under the retry limit (Requirement 8.4
        // keeps exhausted entries out); or Processing whose claim has expired (rescued after a
        // crash/restart). Ordered by Id for FIFO, capped by BatchSize.
        var batch = await set
            .Where(e => e.Status == AuditLogStatus.Pending
                        || (e.Status == AuditLogStatus.Failed && e.RetryCount < _options.RetryLimit)
                        || (e.Status == AuditLogStatus.Processing
                            && (e.ProcessingSince == null || e.ProcessingSince < staleBefore)))
            .OrderBy(e => e.Id)
            .Take(_options.BatchSize)
            .ToListAsync(stoppingToken)
            .ConfigureAwait(false);

        if (batch.Count == 0)
        {
            return;
        }

        // Claim: mark Processing and stamp ProcessingSince, then persist immediately so concurrent
        // replicas don't double-pick the same rows and so a crash leaves a timestamp that lets a
        // later cycle rescue the entry (Requirement 7.7 / 8.1).
        var claimedAt = DateTime.UtcNow;
        foreach (var entry in batch)
        {
            entry.Status = AuditLogStatus.Processing;
            entry.ProcessingSince = claimedAt;
        }

        await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);

        // Deliver each claimed entry. The sender mutates the tracked entity only (Remove on success,
        // Failed/RetryCount/AdditionalInfo on failure) and does not save.
        foreach (var entry in batch)
        {
            await sender.SendAsync(entry, stoppingToken).ConfigureAwait(false);
        }

        // Flush the removals (success) and the Failed/RetryCount updates (failure) in one round-trip.
        await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
    }
}
