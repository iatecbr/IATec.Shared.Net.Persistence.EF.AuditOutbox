namespace IATec.Shared.Persistence.EF.AuditOutbox.Entities;

/// <summary>
/// Lifecycle status of an <see cref="AuditLogEntry"/> as it moves through the outbox pipeline.
/// Persisted as an <see cref="int"/>.
/// </summary>
public enum AuditLogStatus
{
    /// <summary>
    /// The entry has been captured and is awaiting delivery to the remote audit service.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The entry has been claimed by a worker and is being sent. Prevents double processing
    /// across multiple instances/replicas.
    /// </summary>
    Processing = 1,

    /// <summary>
    /// Delivery of the entry failed. The failure reason is stored in
    /// <see cref="AuditLogEntry.AdditionalInfo"/> and <see cref="AuditLogEntry.RetryCount"/> is incremented.
    /// </summary>
    Failed = 2
}

/// <summary>
/// Intermediate outbox entity that stores audit logs pending delivery to the remote audit service.
/// Rows are written within the same transaction as the business operation and drained
/// asynchronously by the background worker.
/// </summary>
public sealed class AuditLogEntry
{
    /// <summary>
    /// Technical primary key, auto-incremented by the configured relational provider.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Stable JSON string sent to the remote audit service.
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp (UTC) when the entry was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Current lifecycle status of the entry. Mapped as an <see cref="int"/>.
    /// </summary>
    public AuditLogStatus Status { get; set; }

    /// <summary>
    /// Optional free-form information, typically the failure reason when delivery fails.
    /// </summary>
    public string? AdditionalInfo { get; set; }

    /// <summary>
    /// Deterministic hash of <see cref="PayloadJson"/> used for idempotency. Backed by a unique index.
    /// </summary>
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>
    /// Number of delivery attempts that have failed for this entry.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// UTC timestamp when the entry was claimed by a worker (set together with
    /// <see cref="AuditLogStatus.Processing"/>). Used to rescue entries that stay
    /// <see cref="AuditLogStatus.Processing"/> because the worker/host crashed mid-delivery: once
    /// this timestamp is older than the configured processing timeout, the entry becomes eligible
    /// again. <c>null</c> when the entry is not currently claimed.
    /// </summary>
    public DateTime? ProcessingSince { get; set; }
}
