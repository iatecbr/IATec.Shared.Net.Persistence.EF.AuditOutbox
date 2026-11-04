using IATec.Shared.Persistence.EF.AuditOutbox.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Tests.Fakes;

/// <summary>
/// Minimal relational <see cref="DbContext"/> used to verify that
/// <c>AuditLogSender</c> mutates the tracked <see cref="AuditLogEntry"/> without persisting.
/// Backed by a kept-open in-memory SQLite connection so entity state is observable.
/// </summary>
internal sealed class TestDbContext : DbContext
{
    private readonly SqliteConnection _connection;

    private TestDbContext(DbContextOptions<TestDbContext> options, SqliteConnection connection)
        : base(options)
    {
        _connection = connection;
    }

    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    /// <summary>
    /// Creates a context over a fresh in-memory SQLite database with the schema created.
    /// </summary>
    public static TestDbContext CreateInMemory()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new TestDbContext(options, connection);
        context.Database.EnsureCreated();
        return context;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.DedupKey).HasMaxLength(128);
            entity.Property(e => e.AdditionalInfo).HasMaxLength(2000);
        });
    }

    public override void Dispose()
    {
        base.Dispose();
        _connection.Dispose();
    }
}
