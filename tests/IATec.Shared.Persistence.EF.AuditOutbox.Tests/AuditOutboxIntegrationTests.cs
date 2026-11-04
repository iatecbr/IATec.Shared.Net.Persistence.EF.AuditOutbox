using System.Text.Json;
using FluentAssertions;
using IATec.Shared.Persistence.EF.AuditOutbox.Entities;
using IATec.Shared.Persistence.EF.AuditOutbox.Persistence;
using IATec.Shared.Persistence.EF.AuditOutbox.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Tests;

/// <summary>
/// Integration tests against a relational (kept-open in-memory SQLite) database that exercise the
/// full outbox pipeline end to end: automatic table creation
/// (<see cref="AuditLogTableInitializer"/>), same-transaction insertion by the
/// <c>AuditOutboxSaveChangesInterceptor</c>, rollback discarding both the business row and its
/// audit log, and idempotency enforced by the <c>DedupKey</c> unique index.
/// Covers Requirements 3.x, 4.x and 6.x.
/// </summary>
public sealed class AuditOutboxIntegrationTests
{
    // ---------------------------------------------------------------------
    // 1. Automatic table creation (Requirement 3.x)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task EnsureTableCreatedAsync_WhenTableMissing_CreatesUsableAuditLogTable()
    {
        // Start with an empty relational database: no tables created up front.
        using var context = AuditableTestDbContext.CreateInMemory(createSchema: false);

        var initializer = new AuditLogTableInitializer();

        await initializer.EnsureTableCreatedAsync(context);

        // The table is now usable: an AuditLogEntry can be inserted and queried back.
        context.AuditLogEntries.Add(new AuditLogEntry
        {
            PayloadJson = "{}",
            DedupKey = "KEY-CREATE",
            CreatedAt = DateTime.UtcNow,
            Status = AuditLogStatus.Pending,
            RetryCount = 0,
        });
        await context.SaveChangesAsync();

        var stored = await context.AuditLogEntries.AsNoTracking().ToListAsync();
        stored.Should().ContainSingle(e => e.DedupKey == "KEY-CREATE");
    }

    [Fact]
    public async Task EnsureTableCreatedAsync_WhenCalledTwice_IsIdempotentAndDoesNotThrow()
    {
        using var context = AuditableTestDbContext.CreateInMemory(createSchema: false);

        var initializer = new AuditLogTableInitializer();

        await initializer.EnsureTableCreatedAsync(context);

        // Second call must be a silent no-op (table already exists): it must not throw.
        var secondCall = async () => await initializer.EnsureTableCreatedAsync(context);
        await secondCall.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureTableCreatedAsync_WhenTableAlreadyExists_IsSilentNoOp()
    {
        // Here the schema (including AuditLogEntry) is created up front by EnsureCreated.
        using var context = AuditableTestDbContext.CreateInMemory(createSchema: true);

        var initializer = new AuditLogTableInitializer();

        var act = async () => await initializer.EnsureTableCreatedAsync(context);

        await act.Should().NotThrowAsync();
    }

    // ---------------------------------------------------------------------
    // 2. Same-transaction insert (Requirements 4.1 - 4.4)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SaveChanges_WithInterceptor_InsertsSingleAuditLogEntryInSameOperation()
    {
        using var context = AuditableTestDbContext.CreateInMemory();

        var entity = new TestAuditableEntity { Name = "created-entity" };
        context.Entities.Add(entity);
        await context.SaveChangesAsync();

        var logs = await context.AuditLogEntries.AsNoTracking().ToListAsync();
        logs.Should().ContainSingle();

        var log = logs[0];
        log.Status.Should().Be(AuditLogStatus.Pending);
        log.DedupKey.Should().NotBeNullOrWhiteSpace();
        log.PayloadJson.Should().NotBeNullOrWhiteSpace();

        using var payload = JsonDocument.Parse(log.PayloadJson);
        var root = payload.RootElement;

        root.GetProperty("containerKey").GetString().Should().Be("test-container");
        root.GetProperty("source").GetString().Should().Be(TestAuditableEntity.SourceValue);
        root.GetProperty("owner").GetString().Should().Be(entity.Id.ToString());
        root.GetProperty("action").GetString().Should().Be("ADDED");
        root.GetProperty("userId").GetString().Should().Be("test-user");

        // `content` is a JSON STRING holding the serialized object, not a nested object.
        var contentElement = root.GetProperty("content");
        contentElement.ValueKind.Should().Be(JsonValueKind.String);

        using var content = JsonDocument.Parse(contentElement.GetString()!);
        content.RootElement.GetProperty("Name").GetString().Should().Be("created-entity");
    }

    [Fact]
    public async Task SaveChanges_WhenEntityDeleted_CapturesDeletedEntityStateInContent()
    {
        using var context = AuditableTestDbContext.CreateInMemory();

        // Arrange: persist an entity and drop the ADDED audit log so only the DELETE is observed.
        var entity = new TestAuditableEntity { Name = "to-be-deleted" };
        context.Entities.Add(entity);
        await context.SaveChangesAsync();
        var deletedId = entity.Id;

        foreach (var log in await context.AuditLogEntries.ToListAsync())
            context.AuditLogEntries.Remove(log);
        await context.SaveChangesAsync();

        // Act: delete the entity.
        context.Entities.Remove(entity);
        await context.SaveChangesAsync();

        // Assert: a DELETED audit log is produced and its content holds the deleted entity's state
        // (not empty), as the remote audit service requires a non-empty content.
        var logs = await context.AuditLogEntries.AsNoTracking().ToListAsync();
        logs.Should().ContainSingle();

        using var payload = JsonDocument.Parse(logs[0].PayloadJson);
        var root = payload.RootElement;

        root.GetProperty("action").GetString().Should().Be("DELETED");
        root.GetProperty("owner").GetString().Should().Be(deletedId.ToString());

        var contentElement = root.GetProperty("content");
        contentElement.ValueKind.Should().Be(JsonValueKind.String);
        contentElement.GetString().Should().NotBeNullOrEmpty();

        using var content = JsonDocument.Parse(contentElement.GetString()!);
        content.RootElement.GetProperty("Name").GetString().Should().Be("to-be-deleted");
        content.RootElement.GetProperty("Id").GetInt32().Should().Be(deletedId);
    }

    [Fact]
    public async Task SaveChanges_WithInterceptor_BusinessRowAndAuditLogArePersistedTogether()
    {
        using var context = AuditableTestDbContext.CreateInMemory();

        context.Entities.Add(new TestAuditableEntity { Name = "together" });
        await context.SaveChangesAsync();

        (await context.Entities.AsNoTracking().CountAsync()).Should().Be(1);
        (await context.AuditLogEntries.AsNoTracking().CountAsync()).Should().Be(1);
    }

    // ---------------------------------------------------------------------
    // 3. Rollback discards both (Requirement 4.3 / 4.4)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SaveChanges_WhenAmbientTransactionRolledBack_DiscardsBusinessRowAndAuditLog()
    {
        using var context = AuditableTestDbContext.CreateInMemory();

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.Entities.Add(new TestAuditableEntity { Name = "rolled-back" });
            await context.SaveChangesAsync();

            // Within the ambient transaction both rows are visible to this connection.
            (await context.Entities.CountAsync()).Should().Be(1);
            (await context.AuditLogEntries.CountAsync()).Should().Be(1);

            await transaction.RollbackAsync();
        }

        // After rollback neither the business row nor its audit log remain in the database.
        // AsNoTracking queries hit the database directly rather than the (still-tracking) context.
        context.ChangeTracker.Clear();
        (await context.Entities.AsNoTracking().CountAsync()).Should().Be(0);
        (await context.AuditLogEntries.AsNoTracking().CountAsync()).Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // 4. Idempotency by DedupKey (Requirement 6.x)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SaveChanges_WhenAnOperationReproducesAnEarlierPayload_KeepsUniqueAuditLogsAndDoesNotAbort()
    {
        using var context = AuditableTestDbContext.CreateInMemory();

        // The audit payload is built from (containerKey, source, owner, action, userId, content),
        // where owner == Id.ToString() and content == { Id, Name }. Modifying the same entity back
        // to a value it already had regenerates a byte-identical MODIFIED payload — and therefore
        // the SAME DedupKey — so two business operations produce the same event.
        var entity = new TestAuditableEntity { Name = "v1" };
        context.Entities.Add(entity);        // event 1: ADDED (content v1)
        await context.SaveChangesAsync();

        entity.Name = "v2";
        await context.SaveChangesAsync();    // event 2: MODIFIED (content v2)

        entity.Name = "v1";
        await context.SaveChangesAsync();    // event 3: MODIFIED (content v1)

        entity.Name = "v2";
        await context.SaveChangesAsync();    // repeats event 2's payload -> must be deduplicated

        // Every surviving audit log has a distinct DedupKey: the repeated "MODIFIED -> v2" payload
        // was discarded by the pre-check / unique index rather than stored twice.
        var allKeys = await context.AuditLogEntries.AsNoTracking()
            .Select(e => e.DedupKey)
            .ToListAsync();

        allKeys.Should().OnlyHaveUniqueItems(
            "the DedupKey unique index + pre-check must prevent duplicate audit logs");

        // Business entity is intact at its latest value (the business operation was not aborted).
        var reloaded = await context.Entities.AsNoTracking().SingleAsync();
        reloaded.Name.Should().Be("v2");
    }

    [Fact]
    public async Task SaveChanges_WhenSamePayloadRepeats_DuplicateIsDiscardedWithoutAbortingBusinessOperation()
    {
        using var context = AuditableTestDbContext.CreateInMemory();

        var entity = new TestAuditableEntity { Name = "v1" };
        context.Entities.Add(entity);
        await context.SaveChangesAsync();

        var addedLogCount = await context.AuditLogEntries.AsNoTracking().CountAsync();
        addedLogCount.Should().Be(1);

        // Toggle the value back and forth so the "MODIFIED -> v2" payload is produced twice.
        entity.Name = "v2";
        await context.SaveChangesAsync();
        var afterFirstModify = await context.AuditLogEntries.AsNoTracking().CountAsync();

        entity.Name = "v1";
        await context.SaveChangesAsync();

        entity.Name = "v2";
        await context.SaveChangesAsync();

        // The repeated "MODIFIED -> v2" event was discarded by the dedup pre-check: the audit log
        // count did not grow for the repeat, and the business save completed normally.
        var finalCount = await context.AuditLogEntries.AsNoTracking().CountAsync();
        finalCount.Should().Be(afterFirstModify + 1,
            "only the new 'MODIFIED -> v1' event should be added; the repeated 'MODIFIED -> v2' is a duplicate");

        var reloaded = await context.Entities.AsNoTracking().SingleAsync(e => e.Id == entity.Id);
        reloaded.Name.Should().Be("v2");
    }

    [Fact]
    public async Task DuplicateDedupKey_SecondDirectInsert_IsRejectedByUniqueIndex()
    {
        using var context = AuditableTestDbContext.CreateInMemory();

        context.AuditLogEntries.Add(new AuditLogEntry
        {
            PayloadJson = "{\"a\":1}",
            DedupKey = "DUPLICATE-KEY",
            CreatedAt = DateTime.UtcNow,
            Status = AuditLogStatus.Pending,
        });
        await context.SaveChangesAsync();

        // A second row with the same DedupKey must be rejected by the unique index at the DB level.
        context.AuditLogEntries.Add(new AuditLogEntry
        {
            PayloadJson = "{\"a\":2}",
            DedupKey = "DUPLICATE-KEY",
            CreatedAt = DateTime.UtcNow,
            Status = AuditLogStatus.Pending,
        });

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
