using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Interception;

/// <summary>
/// Result of building an audit payload: the stable JSON string sent to the remote audit service
/// and the deterministic deduplication key derived from it.
/// </summary>
/// <param name="PayloadJson">
/// The stable JSON string with a fixed property order
/// (<c>containerKey</c>, <c>source</c>, <c>owner</c>, <c>action</c>, <c>userId</c>, <c>content</c>).
/// Every field is a JSON string value; the remote audit service requires all six to be non-null
/// strings, so <c>content</c> is the serialized object embedded as a string.
/// </param>
/// <param name="DedupKey">
/// The uppercase hexadecimal SHA-256 hash of the UTF-8 bytes of <paramref name="PayloadJson"/>.
/// </param>
public readonly record struct AuditPayload(string PayloadJson, string DedupKey);

/// <summary>
/// Builds the stable JSON payload of an audit event and its deterministic deduplication key.
/// </summary>
/// <remarks>
/// The payload is written with a fixed property order and without indentation so that two
/// semantically identical events always produce the exact same JSON string, and therefore the
/// exact same <see cref="AuditPayload.DedupKey"/>. This is what the unique index on
/// <c>DedupKey</c> relies on to deduplicate events (Requirement 6).
/// This type is pure and stateless; it is deliberately decoupled from the interceptor so it can be
/// unit-tested in isolation (stable JSON + reproducible hash).
/// </remarks>
public static class PayloadFactory
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        // Keep the payload compact and portable; content is serialized by the same options.
        SkipValidation = false
    };

    private static readonly JsonSerializerOptions ContentSerializerOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Builds the stable JSON payload and its deduplication key for a single audit event.
    /// </summary>
    /// <param name="containerKey">The container key included in every payload (from configuration).</param>
    /// <param name="source">The entity source type value; may be <c>null</c>.</param>
    /// <param name="owner">The owner resolved after persistence; may be <c>null</c>.</param>
    /// <param name="action">The action, one of <c>ADDED</c>, <c>MODIFIED</c> or <c>DELETED</c>.</param>
    /// <param name="userId">The current user identifier.</param>
    /// <param name="content">
    /// The log content object. It is serialized to JSON and embedded in the payload as a string
    /// value (the inner JSON is escaped inside quotes), never as a nested JSON object. When
    /// <c>null</c> (e.g. <c>DELETED</c> actions) an empty string is emitted.
    /// </param>
    /// <returns>
    /// An <see cref="AuditPayload"/> with the stable JSON string and its uppercase hex SHA-256 hash.
    /// </returns>
    public static AuditPayload Create(
        string containerKey,
        string? source,
        string? owner,
        string action,
        string userId,
        object? content)
    {
        var payloadJson = BuildPayloadJson(containerKey, source, owner, action, userId, content);
        var dedupKey = ComputeDedupKey(payloadJson);

        return new AuditPayload(payloadJson, dedupKey);
    }

    /// <summary>
    /// Serializes the payload with a fixed property order and no indentation, guaranteeing a
    /// byte-stable result for identical inputs.
    /// </summary>
    private static string BuildPayloadJson(
        string containerKey,
        string? source,
        string? owner,
        string action,
        string userId,
        object? content)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            // Every field is emitted as a JSON string (the remote audit service requires all six
            // fields to be non-null strings). Null source/owner/content become an empty string, and
            // `content` is the object serialized to JSON and embedded as a string value (the inner
            // JSON is escaped inside quotes), never a nested JSON object.
            writer.WriteStartObject();

            writer.WriteString("containerKey", containerKey);
            writer.WriteString("source", source ?? string.Empty);
            writer.WriteString("owner", owner ?? string.Empty);
            writer.WriteString("action", action);
            writer.WriteString("userId", userId);
            writer.WriteString("content", SerializeContent(content));

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Serializes the log content object to a JSON string so it can be embedded as a string value
    /// in the payload. Returns an empty string when there is no content (e.g. DELETED actions), so
    /// the remote audit service always receives a non-null string.
    /// </summary>
    private static string SerializeContent(object? content)
    {
        return content is null
            ? string.Empty
            : JsonSerializer.Serialize(content, content.GetType(), ContentSerializerOptions);
    }

    /// <summary>
    /// Computes the deterministic deduplication key as the uppercase hexadecimal SHA-256 hash of
    /// the UTF-8 bytes of <paramref name="payloadJson"/>.
    /// </summary>
    private static string ComputeDedupKey(string payloadJson)
    {
        var bytes = Encoding.UTF8.GetBytes(payloadJson);
        var hash = SHA256.HashData(bytes);

        return Convert.ToHexString(hash);
    }
}
