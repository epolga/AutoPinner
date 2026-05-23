namespace AutoPinner.EmailNotifier;

/// <summary>
/// Send an operator alert. Implementations:
///   - SesEmailNotifier (default, SES API via AWS SDK)
///   - SmtpEmailNotifier (fallback, e.g. SES SMTP relay)
///   - NoopEmailNotifier (when email config is missing — logs only)
/// </summary>
public interface IEmailNotifier
{
    bool IsConfigured { get; }
    Task SendAsync(string subject, string textBody, string? htmlBody = null, CancellationToken ct = default);
}
