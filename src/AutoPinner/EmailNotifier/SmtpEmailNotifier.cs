using System.Net;
using System.Net.Mail;

namespace AutoPinner.EmailNotifier;

/// <summary>
/// SMTP fallback (typically pointed at the SES SMTP endpoint). Used when
/// AUTO_PINNER_EMAIL_TRANSPORT=smtp. Keeps this isolated from the SES API
/// path so credential rotation only touches one transport at a time.
/// </summary>
public sealed class SmtpEmailNotifier : IEmailNotifier
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _pass;
    private readonly string _from;
    private readonly string _to;

    public bool IsConfigured => true;

    public SmtpEmailNotifier(string host, int port, string user, string pass, string from, string to)
    {
        _host = host;
        _port = port;
        _user = user;
        _pass = pass;
        _from = from;
        _to = to;
    }

    public async Task SendAsync(string subject, string textBody, string? htmlBody = null, CancellationToken ct = default)
    {
        using var client = new SmtpClient(_host, _port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(_user, _pass),
        };
        using var msg = new MailMessage(_from, _to)
        {
            Subject = subject,
            Body = textBody,
            IsBodyHtml = false,
        };
        if (!string.IsNullOrWhiteSpace(htmlBody))
        {
            var htmlView = AlternateView.CreateAlternateViewFromString(htmlBody, null, "text/html");
            msg.AlternateViews.Add(htmlView);
        }
        await client.SendMailAsync(msg, ct).ConfigureAwait(false);
    }
}
