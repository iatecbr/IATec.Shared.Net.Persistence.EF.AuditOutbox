using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IATec.Shared.Persistence.EF.AuditOutbox.Interception;
using Xunit;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Tests;

/// <summary>
/// Unit tests for <see cref="PayloadFactory"/>: stable JSON (fixed property order, no indentation)
/// and reproducible deduplication hash.
/// Covers Requirements 5.x and 6.1.
/// </summary>
public sealed class PayloadFactoryTests
{
    [Fact]
    public void Create_WithContent_ProducesStableJsonInFixedOrderWithoutIndentation()
    {
        var content = new { Name = "Alice", Age = 30 };

        var payload = PayloadFactory.Create(
            containerKey: "container-1",
            source: "Customer",
            owner: "owner-9",
            action: "MODIFIED",
            userId: "user-7",
            content: content);

        // All six fields are JSON strings; `content` is the serialized object embedded as a string
        // value (inner JSON escaped within quotes), not a nested JSON object.
        payload.PayloadJson.Should().Be(
            "{\"containerKey\":\"container-1\",\"source\":\"Customer\",\"owner\":\"owner-9\"," +
            "\"action\":\"MODIFIED\",\"userId\":\"user-7\"," +
            "\"content\":\"{\\u0022Name\\u0022:\\u0022Alice\\u0022,\\u0022Age\\u0022:30}\"}");
    }

    [Fact]
    public void Create_DeletedWithNullContentAndNullSourceOwner_EmitsEmptyStrings()
    {
        var payload = PayloadFactory.Create(
            containerKey: "container-1",
            source: null,
            owner: null,
            action: "DELETED",
            userId: "user-7",
            content: null);

        // The remote audit service requires all six fields to be non-null strings, so null
        // source/owner/content are emitted as empty strings (never JSON null).
        payload.PayloadJson.Should().Be(
            "{\"containerKey\":\"container-1\",\"source\":\"\",\"owner\":\"\"," +
            "\"action\":\"DELETED\",\"userId\":\"user-7\",\"content\":\"\"}");
    }

    [Fact]
    public void Create_IsReproducible_SameInputsYieldSamePayloadAndDedupKey()
    {
        var first = PayloadFactory.Create("c", "s", "o", "ADDED", "u", new { X = 1 });
        var second = PayloadFactory.Create("c", "s", "o", "ADDED", "u", new { X = 1 });

        first.PayloadJson.Should().Be(second.PayloadJson);
        first.DedupKey.Should().Be(second.DedupKey);
    }

    [Fact]
    public void Create_DedupKey_IsUppercaseHexSha256OfPayloadJsonUtf8()
    {
        var payload = PayloadFactory.Create("c", "s", "o", "ADDED", "u", new { X = 1 });

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.PayloadJson)));

        payload.DedupKey.Should().Be(expected);
        payload.DedupKey.Should().MatchRegex("^[0-9A-F]{64}$");
    }

    [Fact]
    public void Create_DifferentOwner_YieldsDifferentDedupKey()
    {
        var a = PayloadFactory.Create("c", "s", "owner-a", "ADDED", "u", new { X = 1 });
        var b = PayloadFactory.Create("c", "s", "owner-b", "ADDED", "u", new { X = 1 });

        a.DedupKey.Should().NotBe(b.DedupKey);
        a.PayloadJson.Should().NotBe(b.PayloadJson);
    }
}
