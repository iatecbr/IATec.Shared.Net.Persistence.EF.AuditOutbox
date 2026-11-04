using IATec.Shared.Domain.Identifies.Base;
using IATec.Shared.Domain.SeedWorks;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Tests.Fakes;

/// <summary>
/// Concrete <see cref="BaseIdentify"/> used as the source type of <see cref="TestAuditableEntity"/>.
/// </summary>
/// <remarks>
/// <see cref="BaseIdentify"/> is abstract with a single protected <c>(string)</c> constructor, so a
/// test-local subclass is the smallest way to produce a non-null source type. The interceptor only
/// captures entities whose <c>GetSourceType()</c> is non-null, so the entity must return one of
/// these.
/// </remarks>
internal sealed class TestSourceIdentify : BaseIdentify
{
    public TestSourceIdentify(string value)
        : base(value)
    {
    }
}

/// <summary>
/// Minimal business entity implementing the <c>IEntity</c> contract (via
/// <see cref="EntityInt32"/>) so the audit interceptor captures it.
/// </summary>
/// <remarks>
/// The contract was discovered by reflecting over <c>IATec.Shared.Domain</c> 2.0.0:
/// <c>IEntity</c> requires <c>GetSourceType() : BaseIdentify</c>, <c>GetOwner() : string</c> and
/// <c>GetLogContent() : object</c>. <see cref="EntityInt32"/> (a concrete <c>Entity&lt;int&gt;</c>)
/// already implements <c>IEntity</c> and exposes an <c>Id</c>; by default its
/// <c>GetSourceType()</c> returns <c>null</c>, which the interceptor skips. These overrides make
/// the entity auditable: a stable source, an owner derived from the key, and some log content.
/// </remarks>
internal sealed class TestAuditableEntity : EntityInt32
{
    /// <summary>
    /// The fixed source value reported by <see cref="GetSourceType"/>.
    /// </summary>
    public const string SourceValue = "TestAuditableEntity";

    /// <summary>
    /// Business data that becomes part of the audit log content, so distinct values produce
    /// distinct payloads (and therefore distinct dedup keys).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Returns a stable, non-null source type so the interceptor captures this entity.
    /// </summary>
    public override BaseIdentify GetSourceType() => new TestSourceIdentify(SourceValue);

    /// <summary>
    /// Returns the entity key as a string, resolved after persistence so database-generated keys
    /// are reflected.
    /// </summary>
    public override string GetOwner() => Id.ToString();

    /// <summary>
    /// Returns the content logged for this entity.
    /// </summary>
    public override object GetLogContent() => new { Id, Name };
}
