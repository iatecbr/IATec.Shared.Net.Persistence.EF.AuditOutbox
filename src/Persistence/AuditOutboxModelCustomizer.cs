using IATec.Shared.Persistence.EF.AuditOutbox.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Persistence;

/// <summary>
/// Injects the <see cref="AuditLogEntry"/> mapping into the client's <see cref="DbContext"/> model
/// without forcing the client to edit its own <c>OnModelCreating</c>.
/// </summary>
/// <remarks>
/// <para>
/// EF Core requires an entity to be part of the model before it can be inserted within the same
/// <c>SaveChanges</c> as the business operation, which is what the outbox interceptor relies on.
/// Rather than asking the client to call
/// <c>modelBuilder.ApplyConfiguration(new AuditLogEntryConfiguration())</c> inside their
/// <c>OnModelCreating</c>, this customizer applies the configuration automatically.
/// </para>
/// <para>
/// It extends <see cref="RelationalModelCustomizer"/> so that the default relational
/// model-building pipeline (including the client's own <c>OnModelCreating</c>) runs first; the
/// <see cref="AuditLogEntry"/> mapping is then applied on top. Register it via
/// <see cref="AuditOutboxModelCustomizerExtensions.AddAuditOutboxModel"/>.
/// </para>
/// </remarks>
public sealed class AuditOutboxModelCustomizer : RelationalModelCustomizer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AuditOutboxModelCustomizer"/> class.
    /// </summary>
    /// <param name="dependencies">The model customizer dependencies supplied by EF Core.</param>
    public AuditOutboxModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        // Run the client's own OnModelCreating and the default relational conventions first.
        base.Customize(modelBuilder, context);

        // Then layer the AuditLogEntry mapping on top so it participates in the same model.
        modelBuilder.ApplyConfiguration(new AuditLogEntryConfiguration());
    }
}

/// <summary>
/// Convenience helpers that wire the <see cref="AuditOutboxModelCustomizer"/> into a
/// <see cref="DbContext"/> so <see cref="AuditLogEntry"/> is included in its model.
/// </summary>
public static class AuditOutboxModelCustomizerExtensions
{
    /// <summary>
    /// Includes the <see cref="AuditLogEntry"/> entity in the model of the configured
    /// <see cref="DbContext"/> without requiring changes to its <c>OnModelCreating</c>.
    /// </summary>
    /// <remarks>
    /// Call this while configuring the <see cref="DbContext"/>, e.g. inside
    /// <c>AddDbContext</c>'s options callback or the context's <c>OnConfiguring</c>:
    /// <code>
    /// services.AddDbContext&lt;MyContext&gt;(options =&gt;
    /// {
    ///     options.UseSqlServer(connectionString);
    ///     options.AddAuditOutboxModel();
    /// });
    /// </code>
    /// It replaces the <see cref="IModelCustomizer"/> service with
    /// <see cref="AuditOutboxModelCustomizer"/>, which still runs the client's own
    /// <c>OnModelCreating</c> before adding the audit mapping.
    /// </remarks>
    /// <param name="optionsBuilder">The options builder being configured.</param>
    /// <returns>The same <paramref name="optionsBuilder"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="optionsBuilder"/> is <see langword="null"/>.
    /// </exception>
    public static DbContextOptionsBuilder AddAuditOutboxModel(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        return optionsBuilder.ReplaceService<IModelCustomizer, AuditOutboxModelCustomizer>();
    }

    /// <summary>
    /// Strongly-typed overload of
    /// <see cref="AddAuditOutboxModel(DbContextOptionsBuilder)"/> for use with
    /// <see cref="DbContextOptionsBuilder{TContext}"/>.
    /// </summary>
    /// <typeparam name="TContext">The <see cref="DbContext"/> type being configured.</typeparam>
    /// <param name="optionsBuilder">The options builder being configured.</param>
    /// <returns>The same <paramref name="optionsBuilder"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="optionsBuilder"/> is <see langword="null"/>.
    /// </exception>
    public static DbContextOptionsBuilder<TContext> AddAuditOutboxModel<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        return optionsBuilder.ReplaceService<IModelCustomizer, AuditOutboxModelCustomizer>();
    }
}
