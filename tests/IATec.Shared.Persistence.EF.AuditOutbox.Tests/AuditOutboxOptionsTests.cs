using FluentAssertions;
using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Tests;

/// <summary>
/// Unit tests for <see cref="AuditOutboxOptions"/> defaults/binding and
/// <see cref="AuditOutboxOptionsValidator"/> rules.
/// Covers Requirements 7.5/7.6 adjacents plus options validation (2.1-2.3, 8.5).
/// </summary>
public sealed class AuditOutboxOptionsTests
{
    [Fact]
    public void Defaults_MatchDocumentedValues()
    {
        var options = new AuditOutboxOptions();

        AuditOutboxOptions.SectionName.Should().Be("AuditOutboxLog");
        options.PollInterval.Should().Be(TimeSpan.FromSeconds(10));
        options.BatchSize.Should().Be(100);
        options.RetryLimit.Should().Be(3);
        options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(10));
        options.CircuitBreakerFailuresAllowedBeforeBreaking.Should().Be(3);
        options.CircuitBreakerDuration.Should().Be(TimeSpan.FromSeconds(30));
        options.ContainerKey.Should().BeEmpty();
        options.LogServiceUrl.Should().BeEmpty();
    }

    [Fact]
    public void Binding_FromAuditOutboxLogSection_AppliesOverrides()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuditOutboxLog:ContainerKey"] = "container-key-config",
                ["AuditOutboxLog:LogServiceUrl"] = "https://audit.example.com",
                ["AuditOutboxLog:PollInterval"] = "00:00:15",
                ["AuditOutboxLog:BatchSize"] = "250",
                ["AuditOutboxLog:RetryLimit"] = "7",
                ["AuditOutboxLog:RequestTimeout"] = "00:00:20",
                ["AuditOutboxLog:CircuitBreakerFailuresAllowedBeforeBreaking"] = "9",
                ["AuditOutboxLog:CircuitBreakerDuration"] = "00:01:00",
            })
            .Build();

        var options = configuration.GetSection(AuditOutboxOptions.SectionName).Get<AuditOutboxOptions>();

        options.Should().NotBeNull();
        options!.ContainerKey.Should().Be("container-key-config");
        options.LogServiceUrl.Should().Be("https://audit.example.com");
        options.PollInterval.Should().Be(TimeSpan.FromSeconds(15));
        options.BatchSize.Should().Be(250);
        options.RetryLimit.Should().Be(7);
        options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(20));
        options.CircuitBreakerFailuresAllowedBeforeBreaking.Should().Be(9);
        options.CircuitBreakerDuration.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Validate_MissingContainerKey_FailsMentioningContainerKey()
    {
        var validator = new AuditOutboxOptionsValidator();
        var options = new AuditOutboxOptions { LogServiceUrl = "https://audit.example.com" };

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(AuditOutboxOptions.ContainerKey));
    }

    [Fact]
    public void Validate_MissingLogServiceUrl_FailsMentioningLogServiceUrl()
    {
        var validator = new AuditOutboxOptionsValidator();
        var options = new AuditOutboxOptions { ContainerKey = "container" };

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(AuditOutboxOptions.LogServiceUrl));
    }

    [Fact]
    public void Validate_NonAbsoluteLogServiceUrl_Fails()
    {
        var validator = new AuditOutboxOptionsValidator();
        var options = new AuditOutboxOptions
        {
            ContainerKey = "container",
            LogServiceUrl = "not-an-absolute-url",
        };

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(AuditOutboxOptions.LogServiceUrl));
    }

    [Fact]
    public void Validate_ValidContainerKeyAndAbsoluteUrl_Succeeds()
    {
        var validator = new AuditOutboxOptionsValidator();
        var options = new AuditOutboxOptions
        {
            ContainerKey = "container",
            LogServiceUrl = "https://audit.example.com",
        };

        var result = validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }
}
