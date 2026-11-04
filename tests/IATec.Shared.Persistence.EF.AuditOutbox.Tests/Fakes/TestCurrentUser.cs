using IATec.Shared.Persistence.EF.AuditOutbox.Abstractions;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Tests.Fakes;

/// <summary>
/// Trivial <see cref="ICurrentUser"/> returning a fixed user id for integration tests.
/// </summary>
internal sealed class TestCurrentUser : ICurrentUser
{
    public TestCurrentUser(string userId = "test-user")
    {
        UserId = userId;
    }

    public string UserId { get; }
}
