namespace IATec.Shared.Persistence.EF.AuditOutbox.Abstractions;

/// <summary>
/// Abstraction that supplies the identifier of the user associated with an audited operation.
/// Used by the payload factory to populate the <c>userId</c> field of each audit log entry.
/// </summary>
/// <remarks>
/// Client applications are expected to provide their own implementation (for example, one that
/// resolves the authenticated user from the current request). When the client does not register
/// an implementation, the registration extension registers <see cref="SystemCurrentUser"/>
/// as a fallback so the audit pipeline never fails due to a missing user context. The fallback
/// does not introduce a mandatory ASP.NET dependency on the package.
/// </remarks>
public interface ICurrentUser
{
    /// <summary>
    /// Identifier of the current user. Implementations must return a non-null value; the fallback
    /// returns a configurable default (<c>"system"</c> by convention).
    /// </summary>
    string UserId { get; }
}
