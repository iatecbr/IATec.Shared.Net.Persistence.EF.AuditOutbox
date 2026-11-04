namespace IATec.Shared.Persistence.EF.AuditOutbox.Abstractions;

/// <summary>
/// Fallback <see cref="ICurrentUser"/> implementation used when the client application does not
/// register its own. It returns a configurable default identifier (<c>"system"</c> by convention)
/// and does not depend on ASP.NET, so referencing the audit outbox package never forces an
/// ASP.NET dependency on the consumer.
/// </summary>
/// <remarks>
/// A client that needs request-aware user resolution (for example, reading claims from
/// <c>IHttpContextAccessor</c>) should register its own <see cref="ICurrentUser"/> implementation,
/// which takes precedence over this fallback.
/// </remarks>
public sealed class SystemCurrentUser : ICurrentUser
{
    /// <summary>
    /// Default user identifier applied when no explicit value is supplied.
    /// </summary>
    public const string DefaultUserId = "system";

    /// <summary>
    /// Initializes a new instance using the default identifier (<see cref="DefaultUserId"/>).
    /// </summary>
    public SystemCurrentUser()
        : this(DefaultUserId)
    {
    }

    /// <summary>
    /// Initializes a new instance using a caller-provided identifier.
    /// </summary>
    /// <param name="userId">
    /// The identifier to report. When null, empty, or whitespace, <see cref="DefaultUserId"/> is used.
    /// </param>
    public SystemCurrentUser(string? userId)
    {
        UserId = string.IsNullOrWhiteSpace(userId) ? DefaultUserId : userId;
    }

    /// <inheritdoc />
    public string UserId { get; }
}
