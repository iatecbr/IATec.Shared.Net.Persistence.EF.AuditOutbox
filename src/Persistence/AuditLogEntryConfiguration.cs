using IATec.Shared.Persistence.EF.AuditOutbox.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Persistence;

/// <summary>
/// Provider-agnostic EF Core mapping for <see cref="AuditLogEntry"/>. Designed to work
/// homogeneously across SQL Server, PostgreSQL and MySQL: the table has no schema (simple
/// name <c>AuditLogEntry</c>) and no provider-specific column types are declared.
/// </summary>
public sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    /// <summary>
    /// Maximum length of the <see cref="AuditLogEntry.DedupKey"/> column. A SHA-256 hex hash is
    /// 64 characters; 128 leaves headroom for alternative hashing schemes.
    /// </summary>
    private const int DedupKeyMaxLength = 128;

    /// <summary>
    /// Maximum length of the <see cref="AuditLogEntry.AdditionalInfo"/> column.
    /// </summary>
    private const int AdditionalInfoMaxLength = 2000;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        // Table without schema so the name resolves identically on SQL Server, PostgreSQL and MySQL.
        builder.ToTable("AuditLogEntry");

        // Technical auto-incremented primary key (identity / bigserial / BIGINT AUTO_INCREMENT).
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id)
            .ValueGeneratedOnAdd();

        builder.Property(e => e.PayloadJson)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        // Persist the enum as its underlying int value.
        builder.Property(e => e.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(e => e.AdditionalInfo)
            .HasMaxLength(AdditionalInfoMaxLength)
            .IsRequired(false);

        builder.Property(e => e.DedupKey)
            .HasMaxLength(DedupKeyMaxLength)
            .IsRequired();

        builder.Property(e => e.RetryCount)
            .IsRequired();

        // Nullable claim timestamp used to rescue entries stuck in Processing after a crash.
        builder.Property(e => e.ProcessingSince)
            .IsRequired(false);

        // Idempotency: a deterministic payload hash cannot appear twice.
        builder.HasIndex(e => e.DedupKey)
            .IsUnique();
    }
}
