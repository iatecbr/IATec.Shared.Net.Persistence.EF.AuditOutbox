using IATec.Shared.Persistence.EF.AuditOutbox.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Persistence;

/// <summary>
/// Creates the <see cref="AuditLogEntry"/> table silently and idempotently using EF Core's native
/// relational APIs, so the client does not have to author a migration to adopt the outbox feature.
/// </summary>
/// <remarks>
/// <para>
/// The initializer relies exclusively on EF Core relational services
/// (<see cref="IRelationalDatabaseCreator"/>, <see cref="IMigrationsModelDiffer"/>,
/// <see cref="IMigrationsSqlGenerator"/> and <see cref="IMigrationCommandExecutor"/>). It issues no
/// raw SQL and contains no per-engine DDL branching: the configured provider (SQL Server,
/// PostgreSQL or MySQL) is responsible for translating the model into the appropriate statements.
/// </para>
/// <para>
/// It is silent and idempotent: if the table already exists it is a no-op, and if existence cannot
/// be verified or creation cannot proceed it logs a warning instead of crashing the application.
/// Satisfies Requirements 3.1, 3.2, 3.3 and 3.5.
/// </para>
/// </remarks>
public sealed class AuditLogTableInitializer
{
    private readonly ILogger<AuditLogTableInitializer>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditLogTableInitializer"/> class.
    /// </summary>
    /// <param name="logger">
    /// Optional logger used to report warnings when verification or creation cannot proceed.
    /// </param>
    public AuditLogTableInitializer(ILogger<AuditLogTableInitializer>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Ensures the <see cref="AuditLogEntry"/> table exists in the database backing
    /// <paramref name="context"/>, creating only that table if it is missing.
    /// </summary>
    /// <param name="context">The client <see cref="DbContext"/> whose database is inspected.</param>
    /// <param name="cancellationToken">A token to observe while awaiting the operation.</param>
    /// <returns>A task that completes when verification/creation has finished.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    public async Task EnsureTableCreatedAsync(DbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            // The outbox requires a relational provider; on a non-relational provider the required
            // relational services are unavailable and we silently skip (documented limitation).
            if (!context.Database.IsRelational())
            {
                _logger?.LogWarning(
                    "AuditLogEntry table initialization skipped: the DbContext '{ContextType}' is not using a relational provider.",
                    context.GetType().Name);
                return;
            }

            var entityType = context.Model.FindEntityType(typeof(AuditLogEntry));
            if (entityType is null)
            {
                _logger?.LogWarning(
                    "AuditLogEntry table initialization skipped: the entity is not part of the model of '{ContextType}'. " +
                    "Ensure AddAuditOutboxModel() is configured on the DbContext.",
                    context.GetType().Name);
                return;
            }

            if (await TableExistsAsync(context, entityType, cancellationToken).ConfigureAwait(false))
            {
                // Silent no-op: the table is already present.
                return;
            }

            await CreateTableAsync(context, entityType, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never crash the host because of audit-table provisioning: log and continue. If the
            // table is truly missing, the first insert/worker cycle will surface the problem.
            _logger?.LogWarning(
                ex,
                "Could not verify or create the AuditLogEntry table for '{ContextType}'. " +
                "The application will continue; audit persistence may fail until the table exists.",
                context.GetType().Name);
        }
    }

    /// <summary>
    /// Determines whether the relational table backing <paramref name="entityType"/> already
    /// exists, using EF Core metadata to resolve the table name rather than any raw query string.
    /// </summary>
    private static async Task<bool> TableExistsAsync(
        DbContext context,
        IEntityType entityType,
        CancellationToken cancellationToken)
    {
        var tableName = entityType.GetTableName();
        if (string.IsNullOrEmpty(tableName))
        {
            // No relational table is mapped for the entity; nothing to create.
            return true;
        }

        var schema = entityType.GetSchema();

        var creator = (IRelationalDatabaseCreator)context.GetService<IDatabaseCreator>();

        // If the whole database does not exist yet there is certainly no table.
        if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Ask the provider whether this specific table is present. The translation of this probe is
        // delegated to the provider's IRelationalDatabaseCreator implementation.
        return await creator.HasTablesAsync(cancellationToken).ConfigureAwait(false)
            && await TableNamePresentAsync(context, tableName, schema, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks for the presence of a specific table using the provider's information schema exposed
    /// through EF Core's relational type mapping, without hand-written per-engine DDL.
    /// </summary>
    private static async Task<bool> TableNamePresentAsync(
        DbContext context,
        string tableName,
        string? schema,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // DbConnection.GetSchema is an ADO.NET provider feature (not provider-specific SQL we
            // author) that returns the list of tables known to the connected database.
            var tables = connection.GetSchema("Tables");

            foreach (System.Data.DataRow row in tables.Rows)
            {
                if (RowMatchesTable(tables, row, tableName, schema))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool RowMatchesTable(
        System.Data.DataTable tables,
        System.Data.DataRow row,
        string tableName,
        string? schema)
    {
        var nameColumn = FindColumn(tables, "TABLE_NAME", "table_name", "name");
        if (nameColumn is null)
        {
            return false;
        }

        var rowName = row[nameColumn] as string;
        if (!string.Equals(rowName, tableName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(schema))
        {
            return true;
        }

        var schemaColumn = FindColumn(tables, "TABLE_SCHEMA", "table_schema", "schema");
        if (schemaColumn is null)
        {
            return true;
        }

        var rowSchema = row[schemaColumn] as string;
        return string.Equals(rowSchema, schema, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindColumn(System.Data.DataTable table, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (table.Columns.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates only the <see cref="AuditLogEntry"/> table by diffing an empty model against a model
    /// containing just that entity and letting the provider's SQL generator emit the DDL.
    /// </summary>
    private static async Task CreateTableAsync(
        DbContext context,
        IEntityType entityType,
        CancellationToken cancellationToken)
    {
        var differ = context.GetService<IMigrationsModelDiffer>();
        var sqlGenerator = context.GetService<IMigrationsSqlGenerator>();
        var commandExecutor = context.GetService<IMigrationCommandExecutor>();
        var connection = context.GetService<IRelationalConnection>();
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;
        var relationalModel = designTimeModel.GetRelationalModel();

        // Diff "nothing" against the current relational model to obtain the create operations for
        // the whole model, then keep only the operations that target the AuditLogEntry table. The
        // operations are provider-agnostic; the SQL generator translates them per engine.
        var operations = differ.GetDifferences(null, relationalModel);

        var tableName = entityType.GetTableName();
        var schema = entityType.GetSchema();

        var tableOperations = operations
            .Where(operation => TargetsTable(operation, tableName, schema))
            .ToList();

        if (tableOperations.Count == 0)
        {
            return;
        }

        var commands = sqlGenerator.Generate(tableOperations, designTimeModel);

        await commandExecutor
            .ExecuteNonQueryAsync(commands, connection, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether a migration operation targets the given table (create-table or the
    /// indexes/constraints created alongside it).
    /// </summary>
    private static bool TargetsTable(
        Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation operation,
        string? tableName,
        string? schema)
    {
        return operation switch
        {
            Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation create =>
                TableEquals(create.Name, create.Schema, tableName, schema),
            Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation index =>
                TableEquals(index.Table, index.Schema, tableName, schema),
            Microsoft.EntityFrameworkCore.Migrations.Operations.AddPrimaryKeyOperation pk =>
                TableEquals(pk.Table, pk.Schema, tableName, schema),
            Microsoft.EntityFrameworkCore.Migrations.Operations.AddUniqueConstraintOperation unique =>
                TableEquals(unique.Table, unique.Schema, tableName, schema),
            _ => false
        };
    }

    private static bool TableEquals(string? name, string? schema, string? tableName, string? tableSchema)
    {
        return string.Equals(name, tableName, StringComparison.Ordinal)
            && string.Equals(schema ?? string.Empty, tableSchema ?? string.Empty, StringComparison.Ordinal);
    }
}
