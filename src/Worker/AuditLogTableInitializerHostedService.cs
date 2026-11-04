using IATec.Shared.Persistence.EF.AuditOutbox.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Worker;

/// <summary>
/// Hosted service that ensures the <c>AuditLogEntry</c> table exists in the client's
/// <typeparamref name="TContext"/> database once, at application startup, before the
/// <see cref="AuditOutboxWorker{TContext}"/> begins draining entries.
/// </summary>
/// <typeparam name="TContext">The client's concrete <see cref="DbContext"/> type.</typeparam>
/// <remarks>
/// <para>
/// The <c>AuditLogTableInitializer</c> needs a resolved <see cref="DbContext"/> instance, which is
/// scoped, whereas hosted services are singletons. This service therefore creates a DI scope in
/// <see cref="StartAsync"/>, resolves the scoped <typeparamref name="TContext"/> together with the
/// singleton <see cref="AuditLogTableInitializer"/>, and calls
/// <see cref="AuditLogTableInitializer.EnsureTableCreatedAsync"/>.
/// </para>
/// <para>
/// Initialization is silent and idempotent: if the table already exists it is a no-op, and any
/// provisioning error is logged by the initializer rather than crashing the host. Satisfies
/// Requirements 3.1, 3.2, 3.3 and 3.5.
/// </para>
/// </remarks>
public sealed class AuditLogTableInitializerHostedService<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditLogTableInitializerHostedService<TContext>>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditLogTableInitializerHostedService{TContext}"/> class.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create a DI scope to resolve the scoped context.</param>
    /// <param name="logger">Optional logger used to record startup failures. May be <c>null</c>.</param>
    public AuditLogTableInitializerHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<AuditLogTableInitializerHostedService<TContext>>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger;
    }

    /// <summary>
    /// Creates a scope, resolves the context and initializer, and ensures the table exists.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while awaiting the operation.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var provider = scope.ServiceProvider;

            var context = provider.GetRequiredService<TContext>();
            var initializer = provider.GetRequiredService<AuditLogTableInitializer>();

            await initializer.EnsureTableCreatedAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never block host startup on audit-table provisioning; the initializer already logs the
            // detail, this is a last-resort guard against a resolution failure.
            _logger?.LogWarning(
                ex,
                "Audit outbox table initialization failed for '{ContextType}'. The application will continue.",
                typeof(TContext).Name);
        }
    }

    /// <summary>
    /// No-op: the initializer has no shutdown work.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token (unused).</param>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
