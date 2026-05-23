using Amazon;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;

namespace AutoPinner.EmailNotifier;

/// <summary>
/// SES SendEmail implementation. The sender identity must be verified in SES;
/// AutoPinner reuses whatever identity ALERT_EMAIL_FROM resolves to (typically
/// the cross-stitch.com Uploader-verified sender, ann@cross-stitch.com).
/// </summary>
public sealed class SesEmailNotifier : IEmailNotifier, IDisposable
{
    private readonly AmazonSimpleEmailServiceClient _ses;
    private readonly string _from;
    private readonly string _to;
    private readonly string? _configurationSet;

    public bool IsConfigured => true;

    public SesEmailNotifier(string awsRegion, string from, string to, string? configurationSet = null)
    {
        _ses = new AmazonSimpleEmailServiceClient(RegionEndpoint.GetBySystemName(awsRegion));
        _from = from;
        _to = to;
        _configurationSet = configurationSet;
    }

    public void Dispose() => _ses.Dispose();

    public async Task SendAsync(string subject, string textBody, string? htmlBody = null, CancellationToken ct = default)
    {
        var body = new Body { Text = new Content(textBody) };
        if (!string.IsNullOrWhiteSpace(htmlBody)) body.Html = new Content(htmlBody);

        var request = new SendEmailRequest
        {
            Source = _from,
            Destination = new Destination { ToAddresses = new List<string> { _to } },
            ConfigurationSetName = _configurationSet,
            Message = new Message
            {
                Subject = new Content(subject),
                Body = body,
            },
        };

        await _ses.SendEmailAsync(request, ct).ConfigureAwait(false);
    }
}
