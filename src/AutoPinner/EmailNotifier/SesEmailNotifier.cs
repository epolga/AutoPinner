using Amazon;
using Amazon.SimpleEmail;
using CrossStitch.Shared.Email;

namespace AutoPinner.EmailNotifier;

/// <summary>
/// SES SendEmail wrapper that delegates to the shared <see cref="EmailHelper"/>
/// so both Uploader and AutoPinner go through the same code path when sending
/// operator email. AutoPinner reads sender/recipient/configuration-set from
/// its env (matching Uploader's <c>SenderEmail</c>/<c>AdminEmail</c>/
/// <c>SesConfigurationSetName</c> App.config keys), and the actual SES API
/// call is the shared helper's <c>SendEmailAsync</c> method.
/// </summary>
public sealed class SesEmailNotifier : IEmailNotifier, IDisposable
{
    private readonly AmazonSimpleEmailServiceClient _ses;
    private readonly EmailHelper _emailHelper = new();
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
        await _emailHelper.SendEmailAsync(
            _ses,
            _from,
            new[] { _to },
            subject,
            textBody,
            htmlBody,
            headers: null,
            configurationSetName: _configurationSet,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
