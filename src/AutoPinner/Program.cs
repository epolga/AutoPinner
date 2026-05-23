using System.Globalization;
using AutoPinner;
using AutoPinner.EmailNotifier;
using AutoPinner.Models;
using AutoPinner.Utils;
using DotNetEnv;

// Load .env into process env vars BEFORE Config reads them. No-op if the file
// is absent (e.g. when env vars are injected by a process supervisor in prod).
Env.TraversePath().Load();

var mode = ParseMode(args);
var runId = Guid.NewGuid().ToString("N").Substring(0, 12);

Console.WriteLine($"AutoPinner run {runId} mode={mode}");

Config config;
try
{
    config = new Config();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: config invalid — {ex.Message}");
    // Best-effort one-shot config-failure alert. If email isn't configured
    // either, this falls through to a no-op log.
    await TrySendConfigFailureAsync(ex, runId);
    return 2;
}

Console.WriteLine($"  {config}");

var notifier = BuildNotifier(config);
using var dedup = new AlertDeduplicator(config.AwsRegion, config.DdbTableName, TimeSpan.FromMinutes(config.AlertCooldownMinutes));
using var repo = new DynamoDbDesignRepository(config.AwsRegion, config.DdbTableName);
var boards = await BoardResolver.LoadAsync(config.BoardsCsvPath, config.DefaultBoardId);
Console.WriteLine($"  loaded {boards.MappedCount} album→board mappings from {config.BoardsCsvPath}");
var composer = new PinComposer(config.BaseUrl, boards);
using var pinterest = new PinterestClient(config.PinterestAccessToken);
var rateLimiter = new RateLimiter(TimeSpan.FromSeconds(config.PostIntervalSeconds));

var stats = new RunStats();
var consecutiveFailures = 0;
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    if (mode == RunMode.Once)
    {
        await ProcessBatchAsync(cts.Token);
    }
    else
    {
        while (!cts.IsCancellationRequested)
        {
            await ProcessBatchAsync(cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(config.PostIntervalSeconds), cts.Token).ConfigureAwait(false);
        }
    }
}
catch (OperationCanceledException) { /* graceful shutdown */ }

PrintSummary();
return stats.Failed > 0 ? 1 : 0;

async Task ProcessBatchAsync(CancellationToken ct)
{
    if (stats.PostedSinceMidnightUtc >= config.DailyCap)
    {
        Console.WriteLine($"  daily cap {config.DailyCap} reached; skipping batch");
        return;
    }

    Console.WriteLine($"  fetching up to {config.MaxBatchPerRun} candidates...");
    IReadOnlyList<Design> candidates;
    try
    {
        candidates = await repo.GetLatestUnpinnedAsync(config.MaxBatchPerRun, ct: ct);
    }
    catch (Exception ex)
    {
        await AlertAsync("QueryDDB", "QueryDDB", ex.GetType().Name, ex.Message, design: null, attempts: 0);
        throw;
    }

    Console.WriteLine($"  got {candidates.Count} candidate(s)");

    foreach (var design in candidates)
    {
        if (ct.IsCancellationRequested) break;
        if (stats.PostedSinceMidnightUtc >= config.DailyCap)
        {
            Console.WriteLine($"  daily cap reached mid-batch (posted {stats.PostedSinceMidnightUtc})");
            break;
        }

        if (mode == RunMode.Daemon)
            await rateLimiter.WaitAsync(ct).ConfigureAwait(false);

        await ProcessOneAsync(design, ct);
    }
}

async Task ProcessOneAsync(Design design, CancellationToken ct)
{
    Console.WriteLine($"  → DesignID={design.DesignId} AlbumID={design.AlbumId} Caption=\"{design.Caption}\"");

    bool claimed;
    try
    {
        claimed = await repo.TryClaimAsync(design, ct);
    }
    catch (Exception ex)
    {
        stats.Failed++;
        consecutiveFailures++;
        await AlertAsync("ClaimLock", "UpdateDDB", ex.GetType().Name, ex.Message, design, attempts: design.PinterestAttemptCount + 1);
        await MaybeAlertConsecutiveAsync(design);
        return;
    }

    if (!claimed)
    {
        stats.Skipped++;
        Console.WriteLine("    skip: already locked or pinned by another run");
        return;
    }

    PinterestCreatePinRequest payload;
    try
    {
        payload = composer.Compose(design);
    }
    catch (Exception ex)
    {
        stats.Failed++;
        consecutiveFailures++;
        await repo.MarkFailedAsync(design, $"Compose: {ex.Message}", ct);
        await AlertAsync("Compose", "Config", ex.GetType().Name, ex.Message, design, attempts: design.PinterestAttemptCount + 1);
        await MaybeAlertConsecutiveAsync(design);
        return;
    }

    string pinId;
    try
    {
        pinId = await pinterest.CreatePinAsync(payload, ct);
    }
    catch (PinterestApiException papi)
    {
        stats.Failed++;
        consecutiveFailures++;
        await repo.MarkFailedAsync(design, $"Pinterest {(int)papi.Status}: {papi.ResponseBodySnippet}", ct);
        await AlertAsync("CreatePin", $"HTTP {(int)papi.Status}", papi.Status.ToString(), papi.ResponseBodySnippet, design, attempts: design.PinterestAttemptCount + 1);
        await MaybeAlertConsecutiveAsync(design);
        return;
    }
    catch (Exception ex)
    {
        stats.Failed++;
        consecutiveFailures++;
        await repo.MarkFailedAsync(design, $"CreatePin: {ex.Message}", ct);
        await AlertAsync("CreatePin", "Unexpected", ex.GetType().Name, ex.Message, design, attempts: design.PinterestAttemptCount + 1);
        await MaybeAlertConsecutiveAsync(design);
        return;
    }

    try
    {
        await repo.MarkPostedAsync(design, pinId, ct);
    }
    catch (Exception ex)
    {
        // Pin DID land on Pinterest but we couldn't write the id back. This
        // is the spec's "orphan pin" case — alert immediately and don't keep
        // posting until it's investigated.
        stats.Failed++;
        consecutiveFailures++;
        await AlertAsync("UpdateDDBPosted", "UpdateDDB", ex.GetType().Name,
            $"Pinterest pin {pinId} created, but DDB write failed: {ex.Message}", design, attempts: design.PinterestAttemptCount + 1);
        throw;
    }

    stats.Posted++;
    stats.PostedSinceMidnightUtc++;
    consecutiveFailures = 0;
    Console.WriteLine($"    posted PinID={pinId} (cumulative this run: {stats.Posted})");
}

async Task MaybeAlertConsecutiveAsync(Design design)
{
    if (consecutiveFailures < config.AlertConsecutiveFailureThreshold) return;
    var subject = $"[AutoPinner][{config.EnvironmentName.ToUpperInvariant()}][ERROR] {consecutiveFailures} consecutive failures";
    var body =
        $"AutoPinner has hit {consecutiveFailures} consecutive failures.\n\n" +
        $"Run ID:    {runId}\n" +
        $"Timestamp: {DateTime.UtcNow:o}\n" +
        $"Last design attempted: DesignID={design.DesignId}, AlbumID={design.AlbumId}\n" +
        $"Total this run: posted={stats.Posted}, skipped={stats.Skipped}, failed={stats.Failed}\n";
    await dedup.SendIfNotDuplicateAsync(notifier, $"consecutive:{consecutiveFailures}", subject, body);
}

async Task AlertAsync(string operation, string statusOrClass, string errorClass, string snippet, Design? design, int attempts)
{
    if (!notifier.IsConfigured) return;

    var severity = operation == "Config" || operation == "QueryDDB" ? "FATAL" : "ERROR";
    var subject = $"[AutoPinner][{config.EnvironmentName.ToUpperInvariant()}][{severity}] {operation} failed ({statusOrClass})";
    var body =
        $"AutoPinner reported a failure.\n\n" +
        $"Run ID:    {runId}\n" +
        $"Timestamp: {DateTime.UtcNow:o}\n" +
        $"Operation: {operation}\n" +
        $"Status:    {statusOrClass}\n" +
        $"DesignID:  {(design is null ? "n/a" : design.DesignId.ToString(CultureInfo.InvariantCulture))}\n" +
        $"AlbumID:   {(design is null ? "n/a" : design.AlbumId.ToString(CultureInfo.InvariantCulture))}\n" +
        $"Attempts:  {attempts}\n\n" +
        $"Response/error (truncated):\n{Truncate(snippet, 500)}\n";

    var fingerprint = $"{operation}|{statusOrClass}|{errorClass}";
    await dedup.SendIfNotDuplicateAsync(notifier, fingerprint, subject, body);
}

async Task TrySendConfigFailureAsync(Exception ex, string id)
{
    try
    {
        // We can't use Config to build the notifier — it failed. Read raw env.
        var to = Environment.GetEnvironmentVariable("ALERT_EMAIL_TO");
        var from = Environment.GetEnvironmentVariable("ALERT_EMAIL_FROM");
        var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1";
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(from)) return;
        using var n = new SesEmailNotifier(region, from!, to!);
        await n.SendAsync(
            $"[AutoPinner][CONFIG][FATAL] config invalid",
            $"Run ID: {id}\nTimestamp: {DateTime.UtcNow:o}\n\n{ex.Message}\n",
            null);
    }
    catch
    {
        // Swallow — operator will see the stderr message from the main entry.
    }
}

void PrintSummary()
{
    Console.WriteLine();
    Console.WriteLine("=== Summary ===");
    Console.WriteLine($"  run id:           {runId}");
    Console.WriteLine($"  mode:             {mode}");
    Console.WriteLine($"  posted:           {stats.Posted}");
    Console.WriteLine($"  skipped (locked): {stats.Skipped}");
    Console.WriteLine($"  failed:           {stats.Failed}");
}

IEmailNotifier BuildNotifier(Config cfg)
{
    if (!cfg.EmailEnabled)
    {
        Console.WriteLine("  email: disabled (ALERT_EMAIL_TO / ALERT_EMAIL_FROM not set)");
        return new NoopEmailNotifier();
    }
    if (cfg.EmailTransport == "smtp")
    {
        if (string.IsNullOrWhiteSpace(cfg.SmtpHost) || string.IsNullOrWhiteSpace(cfg.SmtpUser) || string.IsNullOrWhiteSpace(cfg.SmtpPass))
        {
            Console.Error.WriteLine("  email: smtp transport selected but SES_SMTP_HOST/USER/PASS missing — falling back to noop");
            return new NoopEmailNotifier();
        }
        Console.WriteLine($"  email: smtp via {cfg.SmtpHost}:{cfg.SmtpPort}");
        return new SmtpEmailNotifier(cfg.SmtpHost!, cfg.SmtpPort, cfg.SmtpUser!, cfg.SmtpPass!, cfg.AlertEmailFrom!, cfg.AlertEmailTo!);
    }
    Console.WriteLine($"  email: SES ({cfg.AlertEmailFrom} → {cfg.AlertEmailTo})");
    return new SesEmailNotifier(cfg.AwsRegion, cfg.AlertEmailFrom!, cfg.AlertEmailTo!, cfg.SesConfigurationSet);
}

static RunMode ParseMode(string[] argv)
{
    foreach (var a in argv)
    {
        if (string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase)) return RunMode.Once;
        if (string.Equals(a, "--daemon", StringComparison.OrdinalIgnoreCase)) return RunMode.Daemon;
    }
    Console.WriteLine("  no run mode arg; defaulting to --once. Pass --daemon for the loop.");
    return RunMode.Once;
}

static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

internal enum RunMode { Once, Daemon }

internal sealed class RunStats
{
    public int Posted;
    public int Skipped;
    public int Failed;
    public int PostedSinceMidnightUtc;
}
