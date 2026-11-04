using Microsoft.Extensions.Options;

namespace IATec.Shared.Persistence.EF.AuditOutbox.Configuration;

/// <summary>
/// Validates <see cref="AuditOutboxOptions"/> so that initialization fails early with a clear
/// message when required configuration is missing.
/// </summary>
/// <remarks>
/// Registered as <see cref="IValidateOptions{TOptions}"/> and intended to be combined with
/// <c>OptionsBuilder.ValidateOnStart()</c> (wired by <c>AddAuditOutboxLog()</c>) so that a missing
/// <see cref="AuditOutboxOptions.ContainerKey"/> or <see cref="AuditOutboxOptions.LogServiceUrl"/>
/// is surfaced at application startup rather than on first use.
/// Satisfies Requirements 2.1, 2.2, 2.3 and 8.5.
/// </remarks>
public sealed class AuditOutboxOptionsValidator : IValidateOptions<AuditOutboxOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AuditOutboxOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail(
                $"The '{AuditOutboxOptions.SectionName}' configuration section is required but was not provided.");
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ContainerKey))
        {
            failures.Add(RequiredMessage(nameof(AuditOutboxOptions.ContainerKey)));
        }

        if (string.IsNullOrWhiteSpace(options.LogServiceUrl))
        {
            failures.Add(RequiredMessage(nameof(AuditOutboxOptions.LogServiceUrl)));
        }
        else if (!Uri.TryCreate(options.LogServiceUrl, UriKind.Absolute, out _))
        {
            failures.Add(
                $"'{AuditOutboxOptions.SectionName}.{nameof(AuditOutboxOptions.LogServiceUrl)}' " +
                $"must be an absolute URL. Value provided: '{options.LogServiceUrl}'.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static string RequiredMessage(string propertyName) =>
        $"'{AuditOutboxOptions.SectionName}.{propertyName}' is required and must not be empty. " +
        $"Set it in the '{AuditOutboxOptions.SectionName}' configuration section.";
}
