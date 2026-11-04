using FluentAssertions;
using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;
using IATec.Shared.Persistence.EF.AuditOutbox.Entities;
using IATec.Shared.Persistence.EF.AuditOutbox.Tests.Fakes;
using IATec.Shared.Persistence.EF.AuditOutbox.Worker;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Tests;

/// <summary>
/// Unit tests for <see cref="AuditLogSender"/> state rules: it mutates the tracked entity
/// (remove on success; Failed + RetryCount++ + AdditionalInfo on failure) and never persists.
/// Covers Requirements 7.5, 7.6 and 8.4.
/// </summary>
public sealed class AuditLogSenderTests
{
    private static AuditOutboxOptions Options() => new()
    {
        ContainerKey = "container",
        LogServiceUrl = "https://audit.example.com",
    };

    private static AuditLogEntry TrackedEntry(TestDbContext context)
    {
        var entry = new AuditLogEntry
        {
            PayloadJson = "{\"containerKey\":\"container\"}",
            DedupKey = "ABC123",
            CreatedAt = DateTime.UtcNow,
            Status = AuditLogStatus.Processing,
            RetryCount = 0,
        };

        context.AuditLogEntries.Add(entry);
        context.SaveChanges();

        return entry;
    }

    [Fact]
    public async Task SendAsync_WhenResponseSuccess_MarksEntryDeletedWithoutSaving()
    {
        using var context = TestDbContext.CreateInMemory();
        var entry = TrackedEntry(context);

        var client = FakeServiceClient.Returning(BaseResponseDtoFactory.Success());
        var sender = new AuditLogSender(context, new FakeServiceClientFactory(client), Options());

        await sender.SendAsync(entry);

        context.Entry(entry).State.Should().Be(EntityState.Deleted);
        client.PostCallCount.Should().Be(1);

        // Not persisted by the sender: the row is still in the database.
        context.AuditLogEntries.AsNoTracking().Should().ContainSingle(e => e.Id == entry.Id);
    }

    [Fact]
    public async Task SendAsync_PostsToLogEndpoint()
    {
        using var context = TestDbContext.CreateInMemory();
        var entry = TrackedEntry(context);

        var client = FakeServiceClient.Returning(BaseResponseDtoFactory.Success());
        var sender = new AuditLogSender(context, new FakeServiceClientFactory(client), Options());

        await sender.SendAsync(entry);

        client.LastUrl.Should().Be("https://audit.example.com/v1/log");
    }

    [Fact]
    public async Task SendAsync_WhenResponseFailure_MarksFailedIncrementsRetryAndStoresInfo()
    {
        using var context = TestDbContext.CreateInMemory();
        var entry = TrackedEntry(context);

        var client = FakeServiceClient.Returning(BaseResponseDtoFactory.Failure(503, "service unavailable"));
        var sender = new AuditLogSender(context, new FakeServiceClientFactory(client), Options());

        await sender.SendAsync(entry);

        context.Entry(entry).State.Should().NotBe(EntityState.Deleted);
        entry.Status.Should().Be(AuditLogStatus.Failed);
        entry.RetryCount.Should().Be(1);
        entry.AdditionalInfo.Should().NotBeNullOrWhiteSpace();
        entry.AdditionalInfo.Should().Contain("503");
        entry.AdditionalInfo.Should().Contain("service unavailable");
    }

    [Fact]
    public async Task SendAsync_WhenPostThrows_MarksFailedIncrementsRetryAndStoresExceptionMessage()
    {
        using var context = TestDbContext.CreateInMemory();
        var entry = TrackedEntry(context);

        var client = FakeServiceClient.Throwing(new InvalidOperationException("circuit is open"));
        var sender = new AuditLogSender(context, new FakeServiceClientFactory(client), Options());

        await sender.SendAsync(entry);

        entry.Status.Should().Be(AuditLogStatus.Failed);
        entry.RetryCount.Should().Be(1);
        entry.AdditionalInfo.Should().Contain("circuit is open");
    }

    [Fact]
    public async Task SendAsync_RepeatedFailures_IncrementRetryCountEachCall()
    {
        using var context = TestDbContext.CreateInMemory();
        var entry = TrackedEntry(context);

        var client = FakeServiceClient.Throwing(new TimeoutException("timed out"));
        var sender = new AuditLogSender(context, new FakeServiceClientFactory(client), Options());

        await sender.SendAsync(entry);
        await sender.SendAsync(entry);
        await sender.SendAsync(entry);

        entry.RetryCount.Should().Be(3);
        entry.Status.Should().Be(AuditLogStatus.Failed);
    }
}
