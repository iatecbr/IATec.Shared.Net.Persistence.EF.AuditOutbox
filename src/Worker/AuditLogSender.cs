using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;
using IATec.Shared.Persistence.EF.AuditOutbox.Entities;
using IATec.Shared.HttpClient.Dto;
using IATec.Shared.HttpClient.Service;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Worker;

/// <summary>
/// Delivers a single <see cref="AuditLogEntry"/> to the remote audit service and applies the
/// outbox state rules to the tracked entity based on the outcome.
/// </summary>
/// <remarks>
/// <para>
/// Persistence contract: <see cref="SendAsync"/> only mutates the EF-tracked entity (it removes
/// the entry on success or updates its state on failure). It does <b>not</b> call
/// <c>SaveChangesAsync</c>. Flushing those changes to the database is the responsibility of the
/// background worker (task 10), which saves once per batch/cycle.
/// </para>
/// <para>
/// HTTP resilience (retry, circuit breaker, timeout) is handled inside <see cref="IServiceClient"/>
/// via <c>IATec.Shared.HttpClient</c> (Polly). Any failure surfaced either as an unsuccessful
/// response or as a thrown exception (e.g. open circuit or timeout) is treated as a delivery
/// failure, leaving the entry <see cref="AuditLogStatus.Failed"/> and retriable on a later cycle.
/// </para>
/// </remarks>
public sealed class AuditLogSender
{
    /// <summary>Relative path of the remote audit service endpoint.</summary>
    private const string LogEndpointPath = "/v1/log";

    /// <summary>JSON media type used for the request body.</summary>
    private const string JsonMediaType = "application/json";

    /// <summary>Maximum length persisted into <see cref="AuditLogEntry.AdditionalInfo"/>.</summary>
    private const int AdditionalInfoMaxLength = 2000;

    private readonly DbContext _dbContext;
    private readonly IServiceClientFactory _serviceClientFactory;
    private readonly AuditOutboxOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditLogSender"/> class.
    /// </summary>
    /// <param name="dbContext">
    /// The client's <see cref="DbContext"/> tracking the <see cref="AuditLogEntry"/> rows. Used to
    /// remove successfully delivered entries. Persistence is deferred to the worker.
    /// </param>
    /// <param name="serviceClientFactory">
    /// Factory used to resolve the named <see cref="IServiceClient"/> for the audit outbox.
    /// </param>
    /// <param name="options">The audit outbox configuration supplying the remote service URL.</param>
    public AuditLogSender(
        DbContext dbContext,
        IServiceClientFactory serviceClientFactory,
        AuditOutboxOptions options)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _serviceClientFactory = serviceClientFactory ?? throw new ArgumentNullException(nameof(serviceClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Sends a single audit log entry to the remote service and applies the outbox state rules.
    /// </summary>
    /// <param name="entry">The tracked audit log entry to deliver. Required.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <remarks>
    /// On success the entry is removed from the <see cref="DbContext"/> (Requirement 7.5). On
    /// failure the entry is marked <see cref="AuditLogStatus.Failed"/>, its
    /// <see cref="AuditLogEntry.RetryCount"/> is incremented and the reason is stored in
    /// <see cref="AuditLogEntry.AdditionalInfo"/> (Requirement 7.6). This method never persists;
    /// the worker calls <c>SaveChangesAsync</c>.
    /// </remarks>
    public async Task SendAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        cancellationToken.ThrowIfCancellationRequested();

        var url = BuildLogUrl();

        try
        {

            var client = _serviceClientFactory.Create(AuditOutboxServiceClientRegistration.ClientName);

            // PayloadJson is ALREADY the serialized JSON object. Send it as the raw request body via
            // StringContent. Do NOT use a serializing helper here (e.g. GenerateStringContent /
            // PostAsJsonAsync), which would serialize the string again and send an escaped JSON
            // string instead of the object, causing the remote service to reject it with 400.
            using var content = new StringContent(entry.PayloadJson, Encoding.UTF8, JsonMediaType);

            var response = await client.PostAsync(url, content).ConfigureAwait(false);
            
            if (response is { Success: true })
            {
                // Success: drop the entry; the worker flushes the delete. (Requirement 7.5)
                _dbContext.Remove(entry);
                return;
            }

            MarkFailed(entry, DescribeFailure(response));
        }
        catch (Exception ex)
        {
            // Open circuit, timeout or any transport error → treat as a delivery failure so the
            // entry stays Failed and becomes retriable on a later cycle. (Requirement 8.2/8.3)
            MarkFailed(entry, ex.Message);
        }
    }

    /// <summary>
    /// Builds the absolute endpoint URL from the configured log service base URL. Using the
    /// absolute URL matches "POST {LogServiceUrl}/v1/log" literally and avoids relative-URI
    /// combination surprises.
    /// </summary>
    private string BuildLogUrl()
    {
        return _options.LogServiceUrl.TrimEnd('/') + LogEndpointPath;
    }

    /// <summary>
    /// Applies the failure state rules to the tracked entry. (Requirement 7.6 / 8.4)
    /// </summary>
    private static void MarkFailed(AuditLogEntry entry, string? reason)
    {
        entry.Status = AuditLogStatus.Failed;
        entry.RetryCount++;
        entry.AdditionalInfo = Truncate(reason);

        // Release the claim: the entry is no longer in flight, so clear the processing timestamp.
        entry.ProcessingSince = null;
    }

    /// <summary>
    /// Builds a concise failure reason from the unsuccessful response's errors.
    /// </summary>
    private static string DescribeFailure(BaseResponseDto? response)
    {
        if (response?.Errors is not { Count: > 0 } errors)
        {
            return "Audit log delivery failed: the remote service returned an unsuccessful response.";
        }

        var parts = errors.Select(FormatError);

        return string.Join("; ", parts);
    }

    /// <summary>
    /// Formats a single error into a "StatusCode: Message" fragment.
    /// </summary>
    private static string FormatError(ErrorDto error)
    {
        var message = string.IsNullOrWhiteSpace(error.Message) ? "Unknown error" : error.Message;

        return error.StatusCode is { } statusCode
            ? $"{statusCode}: {message}"
            : message;
    }

    /// <summary>
    /// Truncates the reason so it fits the <see cref="AuditLogEntry.AdditionalInfo"/> column.
    /// </summary>
    private static string? Truncate(string? reason)
    {
        if (string.IsNullOrEmpty(reason) || reason.Length <= AdditionalInfoMaxLength)
        {
            return reason;
        }

        return reason[..AdditionalInfoMaxLength];
    }
}
