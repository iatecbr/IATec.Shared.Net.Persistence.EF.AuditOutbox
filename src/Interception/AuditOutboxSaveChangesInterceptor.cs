using System.Runtime.CompilerServices;
using IATec.Shared.Domain.Contracts.Entities;
using IATec.Shared.Persistence.EF.AuditOutbox.Abstractions;
using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;
using IATec.Shared.Persistence.EF.AuditOutbox.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Interception;

/// <summary>
/// Intercepts <see cref="DbContext.SaveChanges()"/> / <see cref="DbContext.SaveChangesAsync"/> to
/// capture audit information for every tracked <see cref="IEntity"/> in an Added, Modified or
/// Deleted state, and (in later phases) to insert the corresponding <c>AuditLogEntry</c> rows in
/// the same transaction as the business operation.
/// </summary>
/// <remarks>
/// This interceptor is the single audit mechanism for audited entities (Requirement 1.4). The core
/// repository no longer captures or dispatches audit logs; auditing lives entirely here and works
/// in two phases:
/// <list type="number">
/// <item>
/// <description>
/// <b>Capture</b> (this phase, <see cref="SavingChanges"/> / <see cref="SavingChangesAsync"/>):
/// inspect the <see cref="ChangeTracker"/> and record, for each audited entity, its source,
/// resolved action and log content. The content MUST be read here, before persistence, because
/// deleted rows (and their values) are gone afterwards and the tracker states reset to
/// <c>Unchanged</c> after a successful save; this also lets Deleted actions capture the entity
/// state being removed.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Materialize</b> (<see cref="SavedChanges"/> / <see cref="SavedChangesAsync"/>): once
/// database-generated keys are assigned, resolve each entity's owner and the current user, build
/// the payload and insert the <c>AuditLogEntry</c> rows in the same unit of work/transaction as
/// the business operation.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>Atomicity.</b> The <c>AuditLogEntry</c> rows are inserted through a second
/// <see cref="DbContext.SaveChanges()"/> on the same context. When the business operation already
/// runs inside an ambient transaction (<see cref="DatabaseFacade.CurrentTransaction"/> is not
/// <c>null</c> — e.g. opened by the core <c>GenericTransaction</c>), the log insert simply enlists
/// in it, so a rollback discards both. When there is no ambient transaction, the interceptor opens
/// a <b>local</b> transaction, saves the logs and commits it, so the business save and the log
/// insert are all-or-nothing (Requirement 4.3/4.4).
/// </para>
/// <para>
/// <b>Re-entrancy.</b> Inserting the <c>AuditLogEntry</c> rows triggers another
/// <c>SaveChanges</c>, which re-enters this interceptor. <c>AuditLogEntry</c> is not an
/// <see cref="IEntity"/> so it is never captured, but to keep the materialize path from re-entering
/// itself a per-context guard flag is raised while the logs are being inserted; the nested
/// capture/materialize cycle becomes a no-op.
/// </para>
/// The captured state is stored per <see cref="DbContext"/> instance via a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>: a context is scoped and single-threaded, so one
/// capture list per context instance is sufficient and safe, and the entry is released automatically
/// when the context is garbage collected.
/// </remarks>
public sealed class AuditOutboxSaveChangesInterceptor : SaveChangesInterceptor
{
    private static readonly IReadOnlyDictionary<EntityState, string> StateToAction =
        new Dictionary<EntityState, string>
        {
            [EntityState.Added] = AuditActions.Added,
            [EntityState.Modified] = AuditActions.Modified,
            [EntityState.Deleted] = AuditActions.Deleted
        };

    private static readonly ConditionalWeakTable<DbContext, List<CapturedEntry>> CapturedByContext = new();

    // Marks contexts that are currently inside the materialize phase, so the nested SaveChanges
    // that persists the AuditLogEntry rows does not re-enter the capture/materialize cycle.
    private static readonly ConditionalWeakTable<DbContext, object> MaterializingContexts = new();

    private static readonly object MaterializingMarker = new();

    private readonly AuditOutboxOptions _options;
    private readonly ICurrentUser _currentUser;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditOutboxSaveChangesInterceptor"/> class.
    /// </summary>
    /// <param name="options">The audit outbox configuration (e.g. the container key for payloads).</param>
    /// <param name="currentUser">Supplies the identifier of the user associated with the operation.</param>
    /// <remarks>
    /// The dependencies are kept on the interceptor so later sub-tasks (payload construction and
    /// <c>AuditLogEntry</c> insertion) can use them without changing the public surface. This phase
    /// only performs the capture and does not yet read them.
    /// </remarks>
    public AuditOutboxSaveChangesInterceptor(AuditOutboxOptions options, ICurrentUser currentUser)
    {
        _options = options;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Captures the audit information from the change tracker before the changes are persisted.
    /// </summary>
    /// <param name="eventData">Contextual information about the save operation.</param>
    /// <param name="result">The current interception result.</param>
    /// <returns>The unmodified interception result; this phase does not alter the save.</returns>
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        CapturePending(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    /// <summary>
    /// Captures the audit information from the change tracker before the changes are persisted.
    /// </summary>
    /// <param name="eventData">Contextual information about the save operation.</param>
    /// <param name="result">The current interception result.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>The unmodified interception result; this phase does not alter the save.</returns>
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CapturePending(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// Materializes and inserts the captured audit entries after the business changes are persisted
    /// and database-generated keys have been assigned.
    /// </summary>
    /// <param name="eventData">Contextual information about the save operation.</param>
    /// <param name="result">The number of state entries written to the database.</param>
    /// <returns>The unmodified save result.</returns>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Materialize(eventData.Context);

        return base.SavedChanges(eventData, result);
    }

    /// <summary>
    /// Materializes and inserts the captured audit entries after the business changes are persisted
    /// and database-generated keys have been assigned.
    /// </summary>
    /// <param name="eventData">Contextual information about the save operation.</param>
    /// <param name="result">The number of state entries written to the database.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>The unmodified save result.</returns>
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await MaterializeAsync(eventData.Context, cancellationToken);

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// Builds the <see cref="AuditLogEntry"/> rows from the entries captured for the given context
    /// and inserts them in the same unit of work/transaction as the business operation.
    /// </summary>
    /// <remarks>
    /// The captured entries are removed from the per-context table first, so a re-entrant save (or
    /// a subsequent save on the same context) does not observe or re-insert them. When there are no
    /// captured entries, or when the context is already inside the materialize phase, this is a
    /// no-op. Atomicity and re-entrancy are described in the type-level remarks.
    /// </remarks>
    private void Materialize(DbContext? context)
    {
        if (!TryBeginMaterialize(context, out var entries))
            return;

        try
        {
            if (!TryBuildLogEntries(entries, out var logEntries))
                return;

            // Primary idempotency guard: drop entries whose DedupKey already exists, so a unique
            // index violation does not invalidate the business transaction on engines that abort it.
            var newEntries = FilterNewEntries(context!, logEntries);
            if (newEntries.Count == 0)
                return;

            context!.Set<AuditLogEntry>().AddRange(newEntries);

            if (context.Database.CurrentTransaction is not null)
            {
                // Enlists in the ambient transaction opened by the caller (e.g. GenericTransaction):
                // the business save and the log insert commit or roll back together.
                TrySaveLogChanges(context);
            }
            else
            {
                // No ambient transaction: wrap the log insert in a local transaction so the already
                // persisted business changes and the logs are all-or-nothing.
                using var transaction = context.Database.BeginTransaction();

                if (TrySaveLogChanges(context))
                    transaction.Commit();
                else
                    transaction.Rollback();
            }
        }
        finally
        {
            EndMaterialize(context!);
        }
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="Materialize"/>.
    /// </summary>
    private async ValueTask MaterializeAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (!TryBeginMaterialize(context, out var entries))
            return;

        try
        {
            if (!TryBuildLogEntries(entries, out var logEntries))
                return;

            // Primary idempotency guard: drop entries whose DedupKey already exists, so a unique
            // index violation does not invalidate the business transaction on engines that abort it.
            var newEntries = await FilterNewEntriesAsync(context!, logEntries, cancellationToken);
            if (newEntries.Count == 0)
                return;

            await context!.Set<AuditLogEntry>().AddRangeAsync(newEntries, cancellationToken);

            if (context.Database.CurrentTransaction is not null)
            {
                await TrySaveLogChangesAsync(context, cancellationToken);
            }
            else
            {
                await using var transaction =
                    await context.Database.BeginTransactionAsync(cancellationToken);

                if (await TrySaveLogChangesAsync(context, cancellationToken))
                    await transaction.CommitAsync(cancellationToken);
                else
                    await transaction.RollbackAsync(cancellationToken);
            }
        }
        finally
        {
            EndMaterialize(context!);
        }
    }

    /// <summary>
    /// Removes and returns the captured entries for the context, raising the per-context
    /// re-entrancy guard. Returns <c>false</c> (without raising the guard) when there is nothing to
    /// materialize or when the context is already materializing.
    /// </summary>
    private static bool TryBeginMaterialize(DbContext? context, out List<CapturedEntry> entries)
    {
        entries = [];

        if (context is null)
            return false;

        // A nested save triggered by the log insert must not materialize again.
        if (MaterializingContexts.TryGetValue(context, out _))
            return false;

        if (!CapturedByContext.TryGetValue(context, out var captured) || captured.Count == 0)
        {
            // Clear any empty bucket so it does not linger on the context.
            CapturedByContext.Remove(context);
            return false;
        }

        // Take ownership of the captured entries and clear them so they are materialized once.
        CapturedByContext.Remove(context);
        entries = captured;

        MaterializingContexts.AddOrUpdate(context, MaterializingMarker);
        return true;
    }

    /// <summary>
    /// Lowers the per-context re-entrancy guard.
    /// </summary>
    private static void EndMaterialize(DbContext context)
    {
        MaterializingContexts.Remove(context);
    }

    /// <summary>
    /// Builds the <see cref="AuditLogEntry"/> rows from the captured entries, resolving each
    /// entity's owner (now that database-generated keys are assigned) and the current user, and
    /// constructing the stable payload and deduplication key via <see cref="PayloadFactory"/>.
    /// </summary>
    /// <returns><c>true</c> when at least one entry was produced; otherwise <c>false</c>.</returns>
    private bool TryBuildLogEntries(List<CapturedEntry> entries, out List<AuditLogEntry> logEntries)
    {
        logEntries = new List<AuditLogEntry>(entries.Count);

        var userId = _currentUser.UserId;
        var createdAt = DateTime.UtcNow;

        foreach (var entry in entries)
        {
            // Owner is resolved here, after persistence, so database-generated keys are reflected.
            var owner = entry.Entity.GetOwner();

            var payload = PayloadFactory.Create(
                _options.ContainerKey,
                entry.Source,
                owner,
                entry.Action,
                userId,
                entry.Content);

            logEntries.Add(new AuditLogEntry
            {
                PayloadJson = payload.PayloadJson,
                DedupKey = payload.DedupKey,
                CreatedAt = createdAt,
                Status = AuditLogStatus.Pending,
                RetryCount = 0
            });
        }

        return logEntries.Count > 0;
    }

    /// <summary>
    /// Filters out entries that would violate the <c>DedupKey</c> unique index, acting as the
    /// primary (pre-insert) idempotency guard.
    /// </summary>
    /// <remarks>
    /// Two levels of deduplication are applied:
    /// <list type="number">
    /// <item><description>
    /// <b>Within the batch:</b> two identical payloads produced by the same save share a
    /// <c>DedupKey</c>; only the first occurrence is kept.
    /// </description></item>
    /// <item><description>
    /// <b>Against the database:</b> a provider-agnostic <c>Contains</c> query resolves which of the
    /// candidate keys already exist and removes them from the batch.
    /// </description></item>
    /// </list>
    /// This pre-check is the primary guard because on some engines a unique-index violation
    /// invalidates the whole transaction, which would otherwise abort the business operation. The
    /// unique index itself remains the final safeguard (handled where the insert is saved).
    /// </remarks>
    private static List<AuditLogEntry> FilterNewEntries(DbContext context, List<AuditLogEntry> logEntries)
    {
        var candidates = DeduplicateWithinBatch(logEntries, out var candidateKeys);
        if (candidates.Count == 0)
            return candidates;

        var existingKeys = context.Set<AuditLogEntry>()
            .Where(e => candidateKeys.Contains(e.DedupKey))
            .Select(e => e.DedupKey)
            .ToList();

        return RemoveExisting(candidates, existingKeys);
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="FilterNewEntries"/>.
    /// </summary>
    private static async Task<List<AuditLogEntry>> FilterNewEntriesAsync(
        DbContext context,
        List<AuditLogEntry> logEntries,
        CancellationToken cancellationToken)
    {
        var candidates = DeduplicateWithinBatch(logEntries, out var candidateKeys);
        if (candidates.Count == 0)
            return candidates;

        var existingKeys = await context.Set<AuditLogEntry>()
            .Where(e => candidateKeys.Contains(e.DedupKey))
            .Select(e => e.DedupKey)
            .ToListAsync(cancellationToken);

        return RemoveExisting(candidates, existingKeys);
    }

    /// <summary>
    /// Removes entries that repeat a <c>DedupKey</c> already seen earlier in the same batch,
    /// returning the surviving entries and the distinct set of their keys.
    /// </summary>
    private static List<AuditLogEntry> DeduplicateWithinBatch(
        List<AuditLogEntry> logEntries,
        out HashSet<string> candidateKeys)
    {
        candidateKeys = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<AuditLogEntry>(logEntries.Count);

        foreach (var entry in logEntries)
        {
            if (candidateKeys.Add(entry.DedupKey))
                candidates.Add(entry);
        }

        return candidates;
    }

    /// <summary>
    /// Returns the entries whose <c>DedupKey</c> is not present in <paramref name="existingKeys"/>.
    /// </summary>
    private static List<AuditLogEntry> RemoveExisting(
        List<AuditLogEntry> candidates,
        List<string> existingKeys)
    {
        if (existingKeys.Count == 0)
            return candidates;

        var existing = new HashSet<string>(existingKeys, StringComparer.Ordinal);
        return candidates.Where(e => !existing.Contains(e.DedupKey)).ToList();
    }

    /// <summary>
    /// Saves the pending <see cref="AuditLogEntry"/> rows, treating a <c>DedupKey</c> unique-index
    /// violation as a silent discard so it never aborts the already persisted business operation
    /// (Requirement 6.3).
    /// </summary>
    /// <returns>
    /// <c>true</c> when the insert committed; <c>false</c> when it was discarded as a duplicate.
    /// Any <see cref="DbUpdateException"/> that is not a unique-constraint violation is rethrown.
    /// </returns>
    private static bool TrySaveLogChanges(DbContext context)
    {
        try
        {
            context.SaveChanges();
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            DetachPendingLogEntries(context);
            return false;
        }
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="TrySaveLogChanges"/>.
    /// </summary>
    private static async Task<bool> TrySaveLogChangesAsync(
        DbContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            DetachPendingLogEntries(context);
            return false;
        }
    }

    /// <summary>
    /// Detaches the <see cref="AuditLogEntry"/> rows left pending after a discarded insert so a
    /// later save on the same context does not retry them.
    /// </summary>
    private static void DetachPendingLogEntries(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<AuditLogEntry>().ToList())
        {
            if (entry.State == EntityState.Added)
                entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Determines whether a <see cref="DbUpdateException"/> was caused by a unique-constraint /
    /// unique-index violation (the <c>DedupKey</c> duplicate case), as opposed to any other update
    /// error which must still surface.
    /// </summary>
    /// <remarks>
    /// The check is provider-agnostic: it scans the exception chain for the vendor error names and
    /// SQLSTATE codes that SQL Server, PostgreSQL (Npgsql) and MySQL (Pomelo/MySqlConnector) use to
    /// report a unique-constraint violation, without taking a hard dependency on any provider.
    /// </remarks>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        for (Exception? current = exception.InnerException; current is not null; current = current.InnerException)
        {
            var typeName = current.GetType().Name;

            // PostgreSQL (Npgsql): SqlState "23505" (unique_violation).
            if (TryGetStringMember(current, "SqlState") is { } sqlState &&
                sqlState == "23505")
            {
                return true;
            }

            // SQL Server (Microsoft.Data.SqlClient): error numbers 2601 / 2627.
            if (typeName == "SqlException" &&
                TryGetIntMember(current, "Number") is 2601 or 2627)
            {
                return true;
            }

            // MySQL (MySqlConnector): error number 1062 (ER_DUP_ENTRY).
            if (typeName == "MySqlException" &&
                TryGetIntMember(current, "Number") is 1062)
            {
                return true;
            }

            // Provider-agnostic fallback: SQLite and other providers surface the duplicate through
            // the exception message (e.g. "UNIQUE constraint failed").
            var message = current.Message;
            if (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate entry", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryGetStringMember(object source, string memberName)
    {
        var property = source.GetType().GetProperty(memberName);
        return property?.GetValue(source) as string;
    }

    private static int? TryGetIntMember(object source, string memberName)
    {
        var property = source.GetType().GetProperty(memberName);
        return property?.GetValue(source) as int?;
    }

    /// <summary>
    /// Reads the change tracker and records the pending audit information for each tracked
    /// <see cref="IEntity"/> in an Added, Modified or Deleted state (including an Unchanged root
    /// whose owned members changed), storing it in the per-context capture list.
    /// </summary>
    /// <remarks>
    /// The content is captured here because deleted rows must be read before persistence (including
    /// for Deleted actions, whose values are gone after the save). The owner is deliberately NOT
    /// resolved yet: for Added entities the identity key is only assigned after the save, so owner
    /// resolution is deferred to the materialize phase, using the same captured entity reference.
    /// </remarks>
    /// <param name="context">The context whose change tracker is being inspected.</param>
    private static void CapturePending(DbContext? context)
    {
        if (context is null)
            return;

        // Skip the nested save that inserts the AuditLogEntry rows: it must not be audited.
        if (MaterializingContexts.TryGetValue(context, out _))
            return;

        var captured = CapturedByContext.GetValue(context, _ => []);

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IEntity entity)
                continue;

            var source = entity.GetSourceType();
            if (source is null)
                continue;

            var action = ResolveAction(entry);
            if (action is null)
                continue;

            // Content MUST be read here, before persistence, because the row (and its values) are
            // gone after the DELETE executes. Capturing in SavingChanges means the entity is still
            // fully populated in the change tracker, so GetLogContent() returns the deleted entity's
            // state for Deleted actions too (the remote audit service requires a non-empty content).
            var content = entity.GetLogContent();

            captured.Add(new CapturedEntry(entity, source.Value, action, content));
        }
    }

    /// <summary>
    /// Resolves the audit action for a root entity entry, returning <c>null</c> when the entry is
    /// not auditable.
    /// </summary>
    /// <remarks>
    /// Besides the direct Added/Modified/Deleted states, an entity is also considered modified when
    /// it appears as Unchanged but owns a value object (owned type) that was changed. Replacing an
    /// owned instance leaves the root Unchanged while the owned entries are marked Added/Deleted, so
    /// this method attributes that change back to the owning entity.
    /// </remarks>
    private static string? ResolveAction(EntityEntry entry)
    {
        if (StateToAction.TryGetValue(entry.State, out var action))
            return action;

        if (entry.State == EntityState.Unchanged && HasModifiedOwnedMembers(entry))
            return AuditActions.Modified;

        return null;
    }

    /// <summary>
    /// Determines whether any owned reference of the given entry has pending changes.
    /// </summary>
    private static bool HasModifiedOwnedMembers(EntityEntry entry)
    {
        // Owned value objects (e.g. mapped via OwnsOne) surface as owned reference entries.
        // Replacing the owned instance marks these targets as Added/Deleted/Modified while the
        // owning root stays Unchanged, so inspecting them detects the change.
        foreach (var reference in entry.References)
        {
            if (reference.TargetEntry is { } target && target.Metadata.IsOwned() && IsChanged(target))
                return true;
        }

        return false;
    }

    private static bool IsChanged(EntityEntry entry)
    {
        return entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;
    }

    /// <summary>
    /// Audit information captured from the change tracker before persistence, keeping a reference
    /// to the tracked entity so its owner can be resolved after the save, once database-generated
    /// keys have been assigned.
    /// </summary>
    /// <param name="Entity">The tracked entity the log refers to.</param>
    /// <param name="Source">The source type value of the entity.</param>
    /// <param name="Action">The action performed: <c>ADDED</c>, <c>MODIFIED</c> or <c>DELETED</c>.</param>
    /// <param name="Content">Content to log, captured before persistence (includes deleted entities).</param>
    private sealed record CapturedEntry(IEntity Entity, string Source, string Action, object? Content);

    /// <summary>
    /// Canonical audit action strings used in the outbox payload.
    /// </summary>
    private static class AuditActions
    {
        public const string Added = "ADDED";
        public const string Modified = "MODIFIED";
        public const string Deleted = "DELETED";
    }
}
