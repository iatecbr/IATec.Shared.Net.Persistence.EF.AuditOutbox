using IATec.Shared.Persistence.EF.AuditOutbox.Abstractions;
using IATec.Shared.Persistence.EF.AuditOutbox.Interception;
using IATec.Shared.Persistence.EF.AuditOutbox.Persistence;
using IATec.Shared.Persistence.EF.AuditOutbox.Worker;
using IATec.Shared.HttpClient.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Configuration;

/// <summary>
/// Registration entry point for the audit outbox feature. Wires every moving part of the outbox
/// (configuration, interceptor, HTTP client, sender, background worker and table initializer) into
/// the dependency injection container and exposes the <see cref="UseAuditOutbox"/> helper the
/// client uses to attach the interceptor and the <c>AuditLogEntry</c> model to its
/// <see cref="DbContext"/>.
/// </summary>
public static class AuditOutboxServiceCollectionExtensions
{
    /// <summary>
    /// Enables the audit outbox for the client's <typeparamref name="TContext"/>: binds and
    /// validates the <c>AuditOutboxLog</c> configuration, registers the save-changes interceptor,
    /// a fallback <see cref="ICurrentUser"/>, the resilient HTTP client, the audit log sender, the
    /// background worker and the table initializer, and replaces the legacy
    /// audit path for every persisted entity.
    /// </summary>
    /// <typeparam name="TContext">The client's concrete <see cref="DbContext"/> type.</typeparam>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">The configuration root/section provider used to bind options.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The client must attach the outbox to its <see cref="DbContext"/> when registering it, using
    /// the companion <see cref="UseAuditOutbox"/> helper so the scoped interceptor and the
    /// <c>AuditLogEntry</c> model are wired per request:
    /// <code>
    /// services.AddAuditOutboxLog&lt;MyContext&gt;(configuration);
    /// services.AddDbContext&lt;MyContext&gt;((sp, o) =&gt;
    /// {
    ///     o.UseSqlServer(connectionString);
    ///     o.UseAuditOutbox(sp);
    /// });
    /// </code>
    /// </para>
    /// <para>
    /// Validation of <see cref="AuditOutboxOptions.ContainerKey"/> and
    /// <see cref="AuditOutboxOptions.LogServiceUrl"/> happens at startup via
    /// <c>ValidateOnStart()</c> and <see cref="AuditOutboxOptionsValidator"/> (Requirement 2).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddAuditOutboxLog<TContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 1. Bind + validate AuditOutboxOptions. The Options pattern feeds runtime consumers while
        //    ValidateOnStart + the validator surface missing ContainerKey/LogServiceUrl at startup
        //    (Requirements 2.1/2.2/2.3).
        services.AddOptions<AuditOutboxOptions>()
            .Bind(configuration.GetSection(AuditOutboxOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<AuditOutboxOptions>, AuditOutboxOptionsValidator>();

        // Dual read: AddHttpClientService needs a concrete options instance at registration time to
        // map the resilience policies and the base address. We read the same section directly here
        // for that one-off wiring; runtime consumers still use IOptions<AuditOutboxOptions>, and
        // validation still runs via ValidateOnStart above.
        var options = configuration.GetSection(AuditOutboxOptions.SectionName).Get<AuditOutboxOptions>()
                      ?? new AuditOutboxOptions();

        // 2. Fallback ICurrentUser: registered only if the client has not registered its own, so a
        //    request-aware implementation always takes precedence.
        services.TryAddScoped<ICurrentUser, SystemCurrentUser>();

        // 3. Interceptor (scoped). A factory resolves the concrete options value and the (possibly
        //    scoped) ICurrentUser so per-request user resolution works. It is attached to the
        //    client's DbContext through UseAuditOutbox(sp) in their AddDbContext options callback.
        services.AddScoped(sp => new AuditOutboxSaveChangesInterceptor(
            sp.GetRequiredService<IOptions<AuditOutboxOptions>>().Value,
            sp.GetRequiredService<ICurrentUser>()));

        // 4. Resilient HTTP client (retry + circuit breaker + timeout mapped from the options).
        services.AddAuditOutboxServiceClient(options);

        // 5. Audit log sender (scoped). The worker can resolve it per scope; it is also constructed
        //    directly by the worker today, so both paths keep working.
        services.AddScoped(sp => new AuditLogSender(
            sp.GetRequiredService<TContext>(),
            sp.GetRequiredService<IServiceClientFactory>(),
            sp.GetRequiredService<IOptions<AuditOutboxOptions>>().Value));

        // 6. Background worker (singleton hosted service) that drains the outbox each poll cycle.
        services.AddHostedService(sp => new AuditOutboxWorker<TContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<AuditOutboxOptions>>().Value,
            sp.GetService<ILogger<AuditOutboxWorker<TContext>>>()));

        // 7. Table initializer: a singleton helper plus a hosted service that creates the
        //    AuditLogEntry table once at startup if it is missing (Requirement 3).
        services.TryAddSingleton<AuditLogTableInitializer>();
        services.AddHostedService<AuditLogTableInitializerHostedService<TContext>>();


        return services;
    }

    /// <summary>
    /// Attaches the audit outbox to a <see cref="DbContext"/> being configured: adds the scoped
    /// <see cref="AuditOutboxSaveChangesInterceptor"/> and includes the <c>AuditLogEntry</c> entity
    /// in the model. Call this inside the service-provider-aware <c>AddDbContext</c> options
    /// callback so the interceptor is resolved from the request scope.
    /// </summary>
    /// <param name="options">The options builder being configured.</param>
    /// <param name="serviceProvider">
    /// The service provider supplied to the <c>AddDbContext</c> callback, used to resolve the
    /// scoped interceptor registered by <see cref="AddAuditOutboxLog{TContext}"/>.
    /// </param>
    /// <returns>The same <paramref name="options"/> instance for chaining.</returns>
    /// <remarks>
    /// <code>
    /// services.AddDbContext&lt;MyContext&gt;((sp, o) =&gt;
    /// {
    ///     o.UseSqlServer(connectionString);
    ///     o.UseAuditOutbox(sp);
    /// });
    /// </code>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> or <paramref name="serviceProvider"/> is <see langword="null"/>.
    /// </exception>
    public static DbContextOptionsBuilder UseAuditOutbox(
        this DbContextOptionsBuilder options,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        options.AddInterceptors(serviceProvider.GetRequiredService<AuditOutboxSaveChangesInterceptor>());

        return options.AddAuditOutboxModel();
    }
}
