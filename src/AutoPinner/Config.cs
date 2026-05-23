using System.Globalization;

namespace AutoPinner;

/// <summary>
/// Strongly-typed env-var loader. Loaded once at startup; immutable afterwards.
///
/// Naming conventions:
///   - SES settings reuse the Uploader's App.config keys verbatim
///     (<c>SenderEmail</c>, <c>AdminEmail</c>, <c>SesConfigurationSetName</c>)
///     so the two apps stay aligned on the same SES identity / configuration set.
///   - Pinterest OAuth settings mirror Uploader's keys
///     (<c>PinterestClientId</c>, <c>PinterestClientSecret</c>, <c>PinterestRedirectUri</c>).
///   - The Pinterest token store path is resolved via the shared
///     <c>PlatformConfig</c> from <c>platform-config.json</c> so both apps
///     share the same on-disk token JSON.
/// </summary>
public sealed class Config
{
    public string AwsRegion { get; }
    public string DdbTableName { get; }
    public int PostIntervalSeconds { get; }
    public int DailyCap { get; }
    public int MaxBatchPerRun { get; }
    public string BaseUrl { get; }
    public string ImageBaseUrl { get; }
    public string PhotoPrefix { get; }
    public string AlbumUrlTemplate { get; }
    public string EnvironmentName { get; }
    public string? DefaultBoardId { get; }

    // Pinterest OAuth (mirrors Uploader/App.private.config keys).
    public string PinterestClientId { get; }
    public string PinterestClientSecret { get; }
    public string PinterestRedirectUri { get; }

    // Email / alerting — reuses Uploader's email setting names so both apps
    // resolve to the same SES identity automatically.
    public string? SenderEmail { get; }
    public string? AdminEmail { get; }
    public string? SesConfigurationSetName { get; }
    public int AlertCooldownMinutes { get; }
    public int AlertConsecutiveFailureThreshold { get; }
    public bool AlertDailySummaryEnabled { get; }
    public int AlertDailySummaryHourUtc { get; }

    public bool EmailEnabled => !string.IsNullOrWhiteSpace(SenderEmail) && !string.IsNullOrWhiteSpace(AdminEmail);

    public Config()
    {
        AwsRegion = Env("AWS_REGION", "us-east-1");
        DdbTableName = Env("DDB_TABLE_NAME", "CrossStitchItems");
        PostIntervalSeconds = EnvInt("POST_INTERVAL_SECONDS", 300);
        DailyCap = EnvInt("DAILY_CAP", 200);
        MaxBatchPerRun = EnvInt("MAX_BATCH_PER_RUN", 1);
        BaseUrl = Env("BASE_URL", "https://cross-stitch.com").TrimEnd('/');
        ImageBaseUrl = Env("IMAGE_BASE_URL", "https://d2o1uvvg91z7o4.cloudfront.net").TrimEnd('/');
        PhotoPrefix = Env("PHOTO_PREFIX", "photos");
        AlbumUrlTemplate = Env("ALBUM_URL_TEMPLATE", string.Empty);
        EnvironmentName = Env("ENVIRONMENT_NAME", "dev");
        DefaultBoardId = EnvOptional("DEFAULT_BOARD_ID");

        PinterestClientId = EnvRequired("PinterestClientId");
        PinterestClientSecret = EnvRequired("PinterestClientSecret");
        PinterestRedirectUri = Env("PinterestRedirectUri", string.Empty);

        // Email vars match Uploader's App.config key names (PascalCase). Most
        // environments tolerate both PascalCase and SCREAMING_SNAKE_CASE env
        // names — accept either to keep the .env file forgiving.
        SenderEmail = EnvOptional("SenderEmail") ?? EnvOptional("SENDER_EMAIL");
        AdminEmail = EnvOptional("AdminEmail") ?? EnvOptional("ADMIN_EMAIL");
        SesConfigurationSetName = EnvOptional("SesConfigurationSetName") ?? EnvOptional("SES_CONFIGURATION_SET_NAME");

        AlertCooldownMinutes = EnvInt("ALERT_COOLDOWN_MINUTES", 60);
        AlertConsecutiveFailureThreshold = EnvInt("ALERT_CONSECUTIVE_FAILURE_THRESHOLD", 5);
        AlertDailySummaryEnabled = EnvBool("ALERT_DAILY_SUMMARY_ENABLED", false);
        AlertDailySummaryHourUtc = EnvInt("ALERT_DAILY_SUMMARY_HOUR_UTC", 7);

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
