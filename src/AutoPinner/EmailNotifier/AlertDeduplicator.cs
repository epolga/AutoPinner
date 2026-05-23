using System.Security.Cryptography;
using System.Text;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace AutoPinner.EmailNotifier;

/// <summary>
/// Persistent alert dedup so back-to-back failures don't spam the inbox.
///
/// Stores one row at PK=ID=SYS#ALERTS, SK=NPage=AUTOPINNER with two fields:
///   LastAlertAtUtc (S)        — ISO-8601 of the most recent alert send
///   LastAlertFingerprint (S)  — SHA-256 of (operation + statusOrErrorClass + path/endpoint)
///
/// Decision rule for SendIfNotDuplicateAsync:
///   - If fingerprint differs from LastAlertFingerprint → always send
///     (a new failure class is worth surfacing immediately).
///   - If fingerprint matches AND now - LastAlertAtUtc < cooldown → suppress.
///   - Otherwise → send (the "still failing" follow-up the task spec asks for).
///
/// Using the existing CrossStitchItems table (with the SYS#ALERTS PK) avoids
/// a second DDB table. The row co-habits without affecting any reader because
/// EntityType is absent (so the GSIs skip it).
/// </summary>
public sealed class AlertDeduplicator : IDisposable
{
    private const string AlertPk = "SYS#ALERTS";
    private const string AlertSk = "AUTOPINNER";

    private readonly AmazonDynamoDBClient _ddb;
    private readonly string _tableName;
    private readonly TimeSpan _cooldown;

    public AlertDeduplicator(string awsRegion, string tableName, TimeSpan cooldown)
    {
        _ddb = new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(awsRegion));
        _tableName = tableName;
        _cooldown = cooldown;
    }

    public void Dispose() => _ddb.Dispose();

    /// <summary>
    /// Returns true if the alert was sent (caller can log it), false if it was
    /// suppressed by cooldown. Implementations of IEmailNotifier that aren't
    /// configured will short-circuit before this is called.
    /// </summary>
    public async Task<bool> SendIfNotDuplicateAsync(
        IEmailNotifier notifier,
        string fingerprintInputs,
        string subject,
        string textBody,
        string? htmlBody = null,
        CancellationToken ct = default)
    {
        if (!notifier.IsConfigured) return false;

        var fingerprint = Sha256Hex(fingerprintInputs);
        var (lastFingerprint, lastAt) = await TryReadAsync(ct).ConfigureAwait(false);

        var sameFingerprint = fingerprint == lastFingerprint;
        var now = DateTimeOffset.UtcNow;
        var withinCooldown = lastAt is not null && (now - lastAt.Value) < _cooldown;

        if (sameFingerprint && withinCooldown) return false;

        await notifier.SendAsync(subject, textBody, htmlBody, ct).ConfigureAwait(false);
        await WriteAsync(fingerprint, now, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<(string? fingerprint, DateTimeOffset? lastAt)> TryReadAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _ddb.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = Key(),
                ConsistentRead = true,
            }, ct).ConfigureAwait(false);

            if (resp.Item is null || resp.Item.Count == 0) return (null, null);

            string? fp = resp.Item.TryGetValue("LastAlertFingerprint", out var f) ? f.S : null;
            DateTimeOffset? at = null;
            if (resp.Item.TryGetValue("LastAlertAtUtc", out var t) && DateTimeOffset.TryParse(t.S, out var parsed))
                at = parsed;
            return (fp, at);
        }
        catch (ResourceNotFoundException)
        {
            return (null, null);
        }
    }

    private async Task WriteAsync(string fingerprint, DateTimeOffset at, CancellationToken ct)
    {
        await _ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["ID"] = new AttributeValue { S = AlertPk },
                ["NPage"] = new AttributeValue { S = AlertSk },
                ["LastAlertFingerprint"] = new AttributeValue { S = fingerprint },
                ["LastAlertAtUtc"] = new AttributeValue { S = at.ToString("o") },
            },
        }, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, AttributeValue> Key() => new()
    {
        ["ID"] = new AttributeValue { S = AlertPk },
        ["NPage"] = new AttributeValue { S = AlertSk },
    };

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
