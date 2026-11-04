using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;
using IATec.Shared.HttpClient.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Worker;

/// <summary>
/// Registers the named <c>IServiceClient</c> used by the audit outbox worker to drain
/// pending audit log entries to the remote audit service, mapping the resilience policies
/// (retry, circuit breaker, request timeout) from <see cref="AuditOutboxOptions"/>.
/// </summary>
internal static class AuditOutboxServiceClientRegistration
{
    /// <summary>
    /// Name of the <c>IServiceClient</c> registered for the audit outbox. The sender resolves
    /// the client from <c>IServiceClientFactory</c> using this name.
    /// </summary>
    public const string ClientName = "AuditOutbox";

    /// <summary>
    /// Registers the audit outbox HTTP client via <c>AddHttpClientService</c>, mapping the
    /// resilience policies from <paramref name="options"/>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="options">The audit outbox options supplying policy and endpoint values.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    internal static IServiceCollection AddAuditOutboxServiceClient(
        this IServiceCollection services,
        AuditOutboxOptions options)
    {
        return services.AddHttpClientService(
            configurePolicy: policy =>
            {
                policy.UseRetry = true;
                policy.RetryCount = options.RetryLimit;
                policy.UseCircuitBreaker = true;
                policy.CircuitBreakerFailuresAllowedBeforeBreaking = options.CircuitBreakerFailuresAllowedBeforeBreaking;
                policy.CircuitBreakerDuration = options.CircuitBreakerDuration;
                policy.RequestTimeout = options.RequestTimeout;
            },
            configureClient: client => client.BaseAddress = new Uri(options.LogServiceUrl),
            clientName: ClientName);
    }
}
