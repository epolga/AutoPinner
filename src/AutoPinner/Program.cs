using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using AutoPinner;
using AutoPinner.EmailNotifier;
using AutoPinner.Models;
using AutoPinner.Utils;
using CrossStitch.Shared;
using CrossStitch.Shared.Pinterest;
using DotNetEnv;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Load .env into process env vars BEFORE Config reads them.
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
    await TrySendConfigFailureAsync(ex, runId);
    return 2;
}

Console.WriteLine($"  {config}");

// Shared lib bootstrap. All wiring lives here so Program.cs documents the
// dependencies and the shared library never reads env / config itself.
var oauthClient = new PinterestOAuthClient(new PinterestOAuthConfig
{
    ClientId = config.PinterestClientId,
    ClientSecret = config.PinterestClientSecret,
    RedirectUri = config.PinterestRedirectUri,
    TokenStorePath = PlatformConfig.ResolvePinterestTokenPath(),
});

var linkHelper = new PatternLinkHelper(new PatternLinkConfig
{
    SiteBaseUrl = config.BaseUrl,
    ImageBaseUrl = config.ImageBaseUrl,
    PhotoPrefix = config.PhotoPrefix,
    AlbumUrlTemplate = config.AlbumUrlTemplate,
});

var uploader = new PinterestUploader(
    new PinterestUploaderConfig { DefaultBoardId = config.DefaultBoardId },
    linkHelper,
    oauthClient);

var notifier = BuildNotifier(config);
using var dedup = new AlertDeduplicator(config.AwsRegion, config.DdbTableName, TimeSpan.FromMinutes(config.AlertCooldownMinutes));
using var repo = new DynamoDbDesignRepository(config.AwsRegion, config.DdbTableName);
var rateLimiter = new RateLimiter(TimeSpan.FromSeconds(config.PostIntervalSeconds));
var retryPolicy = new RetryPolicy();

var stats = new RunStats();
var consecutiveFailures = 0;
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    if (mode == RunMode.BackfillStripHtml)
    {
        var dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
        await RunBackfillStripHtmlAsync(dryRun, cts.Token);
    }
    else if (mode == RunMode.ExportDescriptionsCsv)
    {
        await RunExportDescriptionsCsvAsync(args, cts.Token);
    }
    else if (mode == RunMode.Once)
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

    var pinInput = new PinPatternInfo
    {
        AlbumId = design.AlbumId,
        DesignId = design.DesignId,
        NPage = design.NPage,
        Title = design.Caption,
        Description = design.Description,
        Notes = design.Notes,
        Width = design.Width,
        Height = design.Height,
        NColors = design.NColors,
    };

    string pinId;
    try
    {
        // Retry transient Pinterest failures (429 + 5xx) with exponential
        // backoff; non-transient 4xx falls through immediately.
        pinId = await retryPolicy.ExecuteAsync(
            _ => uploader.UploadPinForPatternAsync(pinInput),
            ex => ex is PinterestApiException papi && papi.IsTransient,
            ct);
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
        await repo.MarkFailedAsync(design, $"UploadPin: {ex.Message}", ct);
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
        // Pin DID land on Pinterest but we couldn't write the id back —
        // orphan pin case. Alert immediately and stop the run.
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

// One-off cleanup: for every design that already has a Pinterest pin id,
// fetch the live pin description, run it through StripHtmlForPlainText,
// and PATCH the pin if the cleaned text differs from the original. DDB is
// read-only here; only Pinterest pin descriptions change.
async Task RunBackfillStripHtmlAsync(bool dryRun, CancellationToken ct)
{
    Console.WriteLine($"  backfill mode: strip HTML from existing pin descriptions  dryRun={dryRun}");

    IReadOnlyList<Design> designs;
    try
    {
        designs = await repo.GetAllPinnedAsync(ct);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FATAL: GetAllPinnedAsync failed: {ex.Message}");
        return;
    }
    Console.WriteLine($"  found {designs.Count} pinned design(s)");

    string accessToken;
    try
    {
        accessToken = await oauthClient.GetValidAccessTokenAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FATAL: could not obtain Pinterest access token: {ex.Message}");
        return;
    }

    var handler = new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    };
    using var http = new HttpClient(handler);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    int scanned = 0, unchanged = 0, changed = 0, notFound = 0, errors = 0;

    foreach (var design in designs)
    {
        if (ct.IsCancellationRequested) break;
        if (string.IsNullOrWhiteSpace(design.PinId)) continue;
        scanned++;

        var pinId = design.PinId!;
        string currentDescription;
        try
        {
            using var getResp = await http.GetAsync($"https://api.pinterest.com/v5/pins/{pinId}", ct);
            if (getResp.StatusCode == HttpStatusCode.NotFound)
            {
                notFound++;
                Console.WriteLine($"  [{scanned,3}] PinID={pinId} Design={design.DesignId} — 404 on Pinterest (skipped)");
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                continue;
            }
            getResp.EnsureSuccessStatusCode();
            var body = await getResp.Content.ReadAsStringAsync(ct);
            var json = JObject.Parse(body);
            currentDescription = json.Value<string>("description") ?? "";
        }
        catch (Exception ex)
        {
            errors++;
            Console.WriteLine($"  [{scanned,3}] PinID={pinId} GET failed: {ex.Message}");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            continue;
        }

        var cleaned = PinterestUploader.StripHtmlForPlainText(currentDescription);
        if (string.Equals(cleaned, currentDescription, StringComparison.Ordinal))
        {
            unchanged++;
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            continue;
        }

        changed++;
        Console.WriteLine($"  [{scanned,3}] PinID={pinId} Design={design.DesignId} — {currentDescription.Length} → {cleaned.Length} chars");
        Console.WriteLine($"          before: {PreviewOneLine(currentDescription)}");
        Console.WriteLine($"          after:  {PreviewOneLine(cleaned)}");

        if (!dryRun)
        {
            try
            {
                var patchJson = JsonConvert.SerializeObject(new { description = cleaned });
                using var content = new StringContent(patchJson, Encoding.UTF8, "application/json");
                using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"https://api.pinterest.com/v5/pins/{pinId}")
                {
                    Content = content,
                };
                using var patchResp = await http.SendAsync(patchReq, ct);
                if (!patchResp.IsSuccessStatusCode)
                {
                    var errBody = await patchResp.Content.ReadAsStringAsync(ct);
                    errors++;
                    Console.WriteLine($"          PATCH {(int)patchResp.StatusCode}: {Truncate(errBody, 200)}");
                }
            }
            catch (Exception ex)
            {
                errors++;
                Console.WriteLine($"          PATCH error: {ex.Message}");
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
    }

    Console.WriteLine();
    Console.WriteLine("=== Backfill summary ===");
    Console.WriteLine($"  dry-run:                  {dryRun}");
    Console.WriteLine($"  pinned designs scanned:   {scanned}");
    Console.WriteLine($"  no HTML found (skipped):  {unchanged}");
    Console.WriteLine($"  {(dryRun ? "would update" : "patched")}:                  {changed}");
    Console.WriteLine($"  not-found on Pinterest:   {notFound}");
    Console.WriteLine($"  errors:                   {errors}");
}

// One-off export: write a CSV with DesignID, AlbumID, PinID, pin URL on
// pinterest.com, and the description we *would* PATCH if we had pin_edit
// permission. Used as a manual-edit worklist. DDB is read-only; no
// Pinterest calls.
async Task RunExportDescriptionsCsvAsync(string[] argv, CancellationToken ct)
{
    var outputPath = ResolveOutputPath(argv, defaultName: "pin-descriptions.csv");
    Console.WriteLine($"  export mode: writing CSV  output={outputPath}");

    IReadOnlyList<Design> designs;
    try
    {
        designs = await repo.GetAllPinnedAsync(ct);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FATAL: GetAllPinnedAsync failed: {ex.Message}");
        return;
    }
    Console.WriteLine($"  found {designs.Count} pinned design(s)");

    var rows = 0;
    using (var writer = new StreamWriter(outputPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
    {
        await writer.WriteLineAsync("DesignID,AlbumID,PinID,PinURL,Description");

        foreach (var design in designs.OrderBy(d => d.AlbumId).ThenBy(d => d.DesignId))
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(design.PinId)) continue;

            var pinInput = new PinPatternInfo
            {
                AlbumId = design.AlbumId,
                DesignId = design.DesignId,
                NPage = design.NPage,
                Title = design.Caption,
                Description = design.Description,
                Notes = design.Notes,
                Width = design.Width,
                Height = design.Height,
                NColors = design.NColors,
            };

            string description;
            try
            {
                description = uploader.ComposeDescriptionFor(pinInput);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Design {design.DesignId}: compose failed — {ex.Message}");
                continue;
            }

            var pinUrl = $"https://www.pinterest.com/pin/{design.PinId}/";

            await writer.WriteLineAsync(string.Join(",", new[]
            {
                design.DesignId.ToString(CultureInfo.InvariantCulture),
                design.AlbumId.ToString(CultureInfo.InvariantCulture),
                CsvField(design.PinId!),
                CsvField(pinUrl),
                CsvField(description),
            }));
            rows++;
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== Export summary ===");
    Console.WriteLine($"  rows written: {rows}");
    Console.WriteLine($"  output:       {outputPath}");
}

static string ResolveOutputPath(string[] argv, string defaultName)
{
    for (var i = 0; i < argv.Length - 1; i++)
    {
        if (string.Equals(argv[i], "--output", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argv[i], "-o", StringComparison.OrdinalIgnoreCase))
        {
            return argv[i + 1];
        }
    }
    return Path.Combine(Directory.GetCurrentDirectory(), defaultName);
}

// RFC 4180 CSV field escape: wrap in double-quotes, double any internal
// double-quote. We also coerce embedded CR/LF to spaces so each pin
// occupies exactly one CSV row (Pinterest descriptions can carry newlines
// for the hashtag block).
static string CsvField(string s)
{
    if (string.IsNullOrEmpty(s)) return "";
    var oneLine = s.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
    return "\"" + oneLine.Replace("\"", "\"\"") + "\"";
}

static string PreviewOneLine(string s)
{
    var oneLine = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    while (oneLine.Contains("  ")) oneLine = oneLine.Replace("  ", " ");
    return Truncate(oneLine.Trim(), 160);
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
        var to = Environment.GetEnvironmentVariable("AdminEmail") ?? Environment.GetEnvironmentVariable("ADMIN_EMAIL");
        var from = Environment.GetEnvironmentVariable("SenderEmail") ?? Environment.GetEnvironmentVariable("SENDER_EMAIL");
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
        Console.WriteLine("  email: disabled (SenderEmail / AdminEmail not set)");
        return new NoopEmailNotifier();
    }
    Console.WriteLine($"  email: SES ({cfg.SenderEmail} → {cfg.AdminEmail})");
    return new SesEmailNotifier(cfg.AwsRegion, cfg.SenderEmail!, cfg.AdminEmail!, cfg.SesConfigurationSetName);
}

static RunMode ParseMode(string[] argv)
{
    foreach (var a in argv)
    {
        if (string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase)) return RunMode.Once;
        if (string.Equals(a, "--daemon", StringComparison.OrdinalIgnoreCase)) return RunMode.Daemon;
        if (string.Equals(a, "--backfill-strip-html", StringComparison.OrdinalIgnoreCase)) return RunMode.BackfillStripHtml;
        if (string.Equals(a, "--export-descriptions-csv", StringComparison.OrdinalIgnoreCase)) return RunMode.ExportDescriptionsCsv;
    }
    Console.WriteLine("  no run mode arg; defaulting to --once. Pass --daemon for the loop.");
    return RunMode.Once;
}

static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

internal enum RunMode { Once, Daemon, BackfillStripHtml, ExportDescriptionsCsv }

internal sealed class RunStats
{
    public int Posted;
    public int Skipped;
    public int Failed;
    public int PostedSinceMidnightUtc;
}
