using IATec.Shared.Persistence.EF.AuditOutbox.Abstractions;
using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;
using IATec.Shared.Persistence.EF.AuditOutbox.Entities;
using IATec.Shared.Persistence.EF.AuditOutbox.Interception;
using IATec.Shared.Persistence.EF.AuditOutbox.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Tests.Fakes;

/// <summary>
/// Relational <see cref="DbContext"/> for the audit outbox integration tests. It models a business
/// entity (<see cref="TestAuditableEntity"/>) together with the real <see cref="AuditLogEntry"/>
/// mapping (via <see cref="AuditLogEntryConfiguration"/>), and attaches the
/// <see cref="AuditOutboxSaveChangesInterceptor"/> under test.
/// </summary>
/// <remarks>
/// Backed by a kept-open in-memory SQLite connection so the schema and transactions behave
/// relationally and survive across the context lifetime. The connection is owned and disposed by
/// the context, so the whole fixture lives in one <c>using</c>.
/// </remarks>
internal sealed class AuditableTestDbContext : DbContext
{
    private readonly SqliteConnection _connection;
    private readonly AuditOutboxSaveChangesInterceptor _interceptor;

    private AuditableTestDbContext(
        DbContextOptions<AuditableTestDbContext> options,
        SqliteConnection connection,
        AuditOutboxSaveChangesInterceptor interceptor)
        : base(options)
    {
        _connection = connection;
        _interceptor = interceptor;
    }

    public DbSet<TestAuditableEntity> Entities => Set<TestAuditableEntity>();

    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    public static AuditOutboxOptions DefaultOptions() => new()
    {
        ContainerKey = "test-container",
        LogServiceUrl = "https://audit.example.com",
    };

    /// <summary>
    /// Creates a context over a fresh in-memory SQLite database with the schema created and the
    /// outbox interceptor attached.
    /// </summary>
    /// <param name="createSchema">
    /// When <c>true</c> (default), the full schema — including the <c>AuditLogEntry</c> table — is
    /// created up front via <see cref="DatabaseFacade.EnsureCreated"/>. Pass <c>false</c> to leave
    /// the database empty so a caller can exercise <see cref="AuditLogTableInitializer"/>.
    /// </param>
    /// <param name="options">Optional outbox options; defaults to <see cref="DefaultOptions"/>.</param>
    /// <param name="currentUser">Optional current user; defaults to a fixed test user.</param>
    public static AuditableTestDbContext CreateInMemory(
        bool createSchema = true,
        AuditOutboxOptions? options = null,
        ICurrentUser? currentUser = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var interceptor = new AuditOutboxSaveChangesInterceptor(
            options ?? DefaultOptions(),
            currentUser ?? new TestCurrentUser());

        var builderOptions = new DbContextOptionsBuilder<AuditableTestDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        var context = new AuditableTestDbContext(builderOptions, connection, interceptor);

        if (createSchema)
        {
            context.Database.EnsureCreated();
        }

        return context;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestAuditableEntity>(entity =>
        {
            entity.ToTable("TestAuditableEntity");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Name);
        });

        // Apply the real AuditLogEntry mapping so the table, column shape and unique DedupKey index
        // match production exactly.
        modelBuilder.ApplyConfiguration(new AuditLogEntryConfiguration());
    }

    public override void Dispose()
    {
        base.Dispose();
        _connection.Dispose();
    }
}
