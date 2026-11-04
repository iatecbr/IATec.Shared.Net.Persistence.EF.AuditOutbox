namespace IATec.Shared.Persistence.EF.AuditOutbox.Configuration;

/// <summary>
/// Strongly-typed configuration for the audit outbox feature, bound from the
/// <c>AuditOutboxLog</c> section of the client application's configuration.
/// </summary>
public sealed class AuditOutboxOptions
{
    /// <summary>
    /// Configuration section name to bind these options from (<c>appsettings.json</c>).
    /// </summary>
    public const string SectionName = "AuditOutboxLog";

    /// <summary>
    /// Container key included in every audit payload. Required.
    /// </summary>
    public string ContainerKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL of the remote audit service. Required.
    /// </summary>
    public string LogServiceUrl { get; set; } = string.Empty;

    /// <summary>
    /// Interval between background worker poll cycles. Defaults to 10 seconds.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum number of audit log entries processed per poll cycle. Defaults to 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of send retries for a failed audit log entry. Defaults to 3.
    /// </summary>
    public int RetryLimit { get; set; } = 3;

    /// <summary>
    /// Maximum time an entry may stay in <see cref="Entities.AuditLogStatus.Processing"/> before it
    /// is considered abandoned (for example because the worker/host crashed mid-delivery) and is
    /// rescued by a later poll cycle. Must be comfortably larger than the time a single delivery
    /// takes so an entry currently being sent by another active replica is not stolen.
    /// Defaults to 5 minutes.
    /// </summary>
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Timeout applied to each HTTP request to the remote audit service. Defaults to 10 seconds.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Number of consecutive failures tolerated before the circuit breaker opens. Defaults to 3.
    /// </summary>
    public int CircuitBreakerFailuresAllowedBeforeBreaking { get; set; } = 3;

    /// <summary>
    /// Duration the circuit breaker stays open before allowing a trial request. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan CircuitBreakerDuration { get; set; } = TimeSpan.FromSeconds(30);
}
