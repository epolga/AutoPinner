using System.Globalization;

namespace AutoPinner;

/// <summary>
/// Strongly-typed env-var loader. All values come from environment variables.
/// Loaded once at startup; immutable afterwards. Validation (e.g. required
/// tokens) happens at construction so missing config fails fast and surfaces
/// in the operator's email if the failure crosses the alerting boundary.
/// </summary>
public sealed class Config
{
    public string AwsRegion { get; }
    public string DdbTableName { get; }
    public string PinterestAccessToken { get; }
    public int PostIntervalSeconds { get; }
    public int DailyCap { get; }
    public int MaxBatchPerRun { get; }
    public string BaseUrl { get; }
    public string EnvironmentName { get; }
    public string BoardsCsvPath { get; }
    public string? DefaultBoardId { get; }

    // Email / alerting (notifier configures itself if AlertEmailTo is empty)
    public string? AlertEmailTo { get; }
    public string? AlertEmailFrom { get; }
    public int AlertCooldownMinutes { get; }
    public int AlertConsecutiveFailureThreshold { get; }
    public bool AlertDailySummaryEnabled { get; }
    public int AlertDailySummaryHourUtc { get; }
    public string? SesConfigurationSet { get; }

    // SMTP fallback (only used if AUTO_PINNER_EMAIL_TRANSPORT=smtp)
    public string EmailTransport { get; }
    public string? SmtpHost { get; }
    public int SmtpPort { get; }
    public string? SmtpUser { get; }
    public string? SmtpPass { get; }

    public bool EmailEnabled => !string.IsNullOrWhiteSpace(AlertEmailTo) && !string.IsNullOrWhiteSpace(AlertEmailFrom);

    public Config()
    {
        AwsRegion = Env("AWS_REGION", "us-east-1");
        DdbTableName = Env("DDB_TABLE_NAME", "CrossStitchItems");
        PinterestAccessToken = EnvRequired("PINTEREST_ACCESS_TOKEN");
        PostIntervalSeconds = EnvInt("POST_INTERVAL_SECONDS", 300);
        DailyCap = EnvInt("DAILY_CAP", 200);
        MaxBatchPerRun = EnvInt("MAX_BATCH_PER_RUN", 1);
        BaseUrl = Env("BASE_URL", "https://cross-stitch.com").TrimEnd('/');
        EnvironmentName = Env("ENVIRONMENT_NAME", "dev");
        BoardsCsvPath = Env("BOARDS_CSV_PATH", "AlbumBoards.csv");
        DefaultBoardId = EnvOptional("DEFAULT_BOARD_ID");

        AlertEmailTo = EnvOptional("ALERT_EMAIL_TO");
        AlertEmailFrom = EnvOptional("ALERT_EMAIL_FROM");
        AlertCooldownMinutes = EnvInt("ALERT_COOLDOWN_MINUTES", 60);
        AlertConsecutiveFailureThreshold = EnvInt("ALERT_CONSECUTIVE_FAILURE_THRESHOLD", 5);
        AlertDailySummaryEnabled = EnvBool("ALERT_DAILY_SUMMARY_ENABLED", false);
        AlertDailySummaryHourUtc = EnvInt("ALERT_DAILY_SUMMARY_HOUR_UTC", 7);
        SesConfigurationSet = EnvOptional("SES_CONFIGURATION_SET");

        EmailTransport = Env("AUTO_PINNER_EMAIL_TRANSPORT", "ses").ToLowerInvariant();
        SmtpHost = EnvOptional("SES_SMTP_HOST");
        SmtpPort = EnvInt("SES_SMTP_PORT", 587);
        SmtpUser = EnvOptional("SES_SMTP_USER");
        SmtpPass = EnvOptional("SES_SMTP_PASS");

        if (PostIntervalSeconds < 60)
            throw new InvalidOperationException($"POST_INTERVAL_SECONDS must be ≥ 60 (got {PostIntervalSeconds}); short intervals will trip Pinterest spam signals.");
        if (DailyCap < 1)
            throw new InvalidOperationException($"DAILY_CAP must be ≥ 1 (got {DailyCap}).");
        if (MaxBatchPerRun < 1)
            throw new InvalidOperationException($"MAX_BATCH_PER_RUN must be ≥ 1 (got {MaxBatchPerRun}).");
    }

    public override string ToString() =>
        $"env={EnvironmentName} region={AwsRegion} table={DdbTableName} interval={PostIntervalSeconds}s dailyCap={DailyCap} batch={MaxBatchPerRun} baseUrl={BaseUrl} emailEnabled={EmailEnabled}";

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static string? EnvOptional(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static string EnvRequired(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"Missing required env var {name}.");

    private static int EnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new InvalidOperationException($"Env var {name}={raw} is not an integer.");
        return v;
    }

    private static bool EnvBool(string name, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("1", StringComparison.Ordinal)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
