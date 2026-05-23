namespace AutoPinner.EmailNotifier;

/// <summary>
/// Used when email config is incomplete. Logs the alert to stderr so the
/// operator can still see something when grepping logs, but never sends.
/// IsConfigured == false signals callers to skip dedup state writes too —
/// no point recording a "last alert sent" timestamp when nothing was sent.
/// </summary>
public sealed class NoopEmailNotifier : IEmailNotifier
{
    public bool IsConfigured => false;

    public Task SendAsync(string subject, string textBody, string? htmlBody = null, CancellationToken ct = default)
    {
        Console.Error.WriteLine($"[email-disabled] {subject}");
        Console.Error.WriteLine(textBody);
        return Task.CompletedTask;
    }
}
