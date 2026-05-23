# AutoPinner

C# .NET 8 worker that pulls cross-stitch designs from DynamoDB (newest first), creates Pinterest pins for the ones that don't have a pin yet, and writes the returned pin id back into the same row. Runs as a one-shot (`--once`) or a long-running daemon (`--daemon`).

Sister project of [`Uploader`](../Uploader) (which writes new designs into DynamoDB after upload) and [`cross-stitch/automation/pinterest-agent`](../cross-stitch/automation/pinterest-agent) (the daily analytics/reporting agent). The Pinterest upload + OAuth + SES code itself lives in the shared [`CrossStitch.Shared`](../CrossStitch.Shared) library; both Uploader and AutoPinner reference it so a change to "how we post pins" only happens in one place.

Task spec: [`cross-stitch-platform-docs/docs/tasks/TASK_AutoPinner.md`](../cross-stitch-platform-docs/docs/tasks/TASK_AutoPinner.md).

## What it does

For each run:

1. Query `CrossStitchItems` via the `DesignsByID-index` GSI (`EntityType = "DESIGN"`, sorted DESC by `DesignID`).
2. Filter out designs that already have a pin id under any of the six historical attribute names (`PinID`, `PinId`, `PinterestPinId`, `PinterestPinID`, `PinterestID`, `PinterestId` — see [`dynamodb-schema.md §4.4`](../cross-stitch-platform-docs/docs/integration/dynamodb-schema.md)) and any that are mid-flight (`PinterestStatus = POSTING` or `POSTED`).
3. For each candidate up to `MAX_BATCH_PER_RUN`:
   - **Claim** via a conditional `UpdateItem` that sets `PinterestStatus = POSTING` only if the row still has no pin and isn't already POSTING/POSTED. If the conditional check fails, skip (another run got it).
   - **Compose** title / description / link / image URL / alt text from the design row plus rotating templates (DesignID modulo).
   - **POST** to `https://api.pinterest.com/v5/pins` with exponential backoff on 429 / 5xx.
   - **On success**, write `PinID = <returned id>` and `PinterestStatus = POSTED` back to the row.
   - **On failure**, write `PinterestStatus = FAILED` + `PinterestLastError`, and trigger the email notifier (deduped).
4. Print a per-run summary.

Daemon mode loops on `POST_INTERVAL_SECONDS` between batches and respects `DAILY_CAP`.

## Safety: idempotency and rate limits

- **No double-pinning.** The conditional claim asserts that none of the six pin-id attribute names exists AND that `PinterestStatus` isn't `POSTING`/`POSTED`. If two runs pick the same design, exactly one wins the conditional update; the other gets a `ConditionalCheckFailedException` and skips silently.
- **No spam cadence.** `POST_INTERVAL_SECONDS` (default 300 = 5 minutes) is the minimum interval between pins in daemon mode. `MAX_BATCH_PER_RUN` (default 1) caps `--once` runs.
- **No runaway costs.** `DAILY_CAP` (default 200) hard-stops new pins once reached (counted in-process; the in-memory counter resets on restart — for true durable cap, run `--once` from cron and let the natural date boundary apply).
- **Backoff on 429.** [`Utils/RetryPolicy.cs`](src/AutoPinner/Utils/RetryPolicy.cs) — exponential with jitter, 5 attempts default.

## Board selection

The shared `PinterestUploader` reads the canonical `AlbumBoards.csv` at [`cross-stitch-platform-docs/data/AlbumBoards.csv`](../cross-stitch-platform-docs/data/AlbumBoards.csv) — same file Uploader uses. Path is resolved via `CrossStitch.Shared.PlatformConfig.ResolveAlbumBoardsCsvPath()`, which reads `cross-stitch-platform-docs/platform-config.json`'s `albumBoardsCsvPath` key.

If an album isn't in the CSV, falls back to `DEFAULT_BOARD_ID`; if that's also unset, throws (no board → no pin).

## Email alerts

When a non-transient API error, persistent DDB failure, or `N` consecutive failures occurs, AutoPinner emails the operator via the shared `CrossStitch.Shared.Email.EmailHelper` (the same path Uploader uses). Defaults:

- Transport: AWS SES via the AWS SDK.
- Config: reuses Uploader's App.config key names verbatim — `SenderEmail`, `AdminEmail`, `SesConfigurationSetName` — so both apps resolve to the same verified identity (`ann@cross-stitch.com` in the cross-stitch workspace today).
- Dedup: an SHA-256 fingerprint of `operation|status|errorClass` is stored at DDB `ID=SYS#ALERTS, NPage=AUTOPINNER` along with `LastAlertAtUtc`. Same-fingerprint alerts within `ALERT_COOLDOWN_MINUTES` (default 60) are suppressed.
- Threshold: `ALERT_CONSECUTIVE_FAILURE_THRESHOLD` (default 5) triggers a separate "still failing" alert.
- If both `SenderEmail` and `AdminEmail` are empty, the notifier is a no-op (failures are still logged to stderr).

## Configuration

All settings come from environment variables. AutoPinner auto-loads a `.env` file at startup (via `DotNetEnv`) — copy [`.env.example`](.env.example) to `.env` and fill in real values.

| Variable | Default | Purpose |
|---|---|---|
| `AWS_REGION` | `us-east-1` | DDB + SES region |
| `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` (or `AWS_PROFILE`) | — | AWS credentials. The pinterest-agent IAM user has the required `dynamodb:Query/UpdateItem/PutItem/GetItem` on `CrossStitchItems` and `ses:SendEmail` on the cross-stitch.com identity. |
| `DDB_TABLE_NAME` | `CrossStitchItems` | Table name |
| `PinterestClientId` / `PinterestClientSecret` / `PinterestRedirectUri` | required (id + secret) | Pinterest OAuth app credentials. Same key names as Uploader's `App.private.config`. The on-disk token JSON is shared with Uploader via `platform-config.json`. |
| `POST_INTERVAL_SECONDS` | `300` | Min gap between pins in daemon mode |
| `DAILY_CAP` | `200` | Max pins per process lifetime |
| `MAX_BATCH_PER_RUN` | `1` | Batch size per `--once` invocation |
| `BASE_URL` | `https://cross-stitch.com` | Used to build pin destination links |
| `IMAGE_BASE_URL` | `https://d2o1uvvg91z7o4.cloudfront.net` | CDN/S3 base for design photos |
| `PHOTO_PREFIX` | `photos` | Path segment under `IMAGE_BASE_URL` |
| `ALBUM_URL_TEMPLATE` | — | Optional override; placeholders `{AlbumId}` `{CaptionSlug}` |
| `ENVIRONMENT_NAME` | `dev` | Appears in email subjects |
| `DEFAULT_BOARD_ID` | — | Fallback board if album not in `cross-stitch-platform-docs/data/AlbumBoards.csv` |
| `SenderEmail` / `AdminEmail` | — | Sender (verified SES identity) + recipient. Both empty disables email. Same key names as Uploader's App.config. |
| `SesConfigurationSetName` | — | Optional SES configuration set (matches Uploader's key name) |
| `ALERT_COOLDOWN_MINUTES` | `60` | Dedup window for same-fingerprint alerts |
| `ALERT_CONSECUTIVE_FAILURE_THRESHOLD` | `5` | Trigger a "still failing" alert at this run-streak |
| `ALERT_DAILY_SUMMARY_ENABLED` | `false` | (Planned) daily roll-up email |
| `ALERT_DAILY_SUMMARY_HOUR_UTC` | `7` | (Planned) hour at which the daily summary fires |

## Running locally

```powershell
cd src/AutoPinner
dotnet restore
dotnet build

# One-shot (recommended for cron):
dotnet run -- --once

# Long-running daemon:
dotnet run -- --daemon
```

The first run will:
- Verify it can read DDB by issuing a single Query.
- Verify the bearer token by attempting one create-pin (if any candidate exists).
- Print a summary regardless.

## Scheduling

### Linux cron — recommended

```cron
# Every 7 minutes, post up to MAX_BATCH_PER_RUN pins.
*/7 * * * * cd /opt/AutoPinner/src/AutoPinner && /usr/bin/dotnet AutoPinner.dll --once >> /var/log/autopinner.log 2>&1
```

`--once` is preferred over `--daemon` for cron: each invocation gets fresh AWS credentials, fresh tokens, and a clean rate-limit clock; if it crashes, the next tick recovers.

### Windows Task Scheduler

```powershell
$action = New-ScheduledTaskAction -Execute "dotnet.exe" `
    -Argument "D:\ann\Git\AutoPinner\src\AutoPinner\bin\Release\net8.0\AutoPinner.dll --once" `
    -WorkingDirectory "D:\ann\Git\AutoPinner\src\AutoPinner\bin\Release\net8.0"

$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 7) `
    -RepetitionDuration (New-TimeSpan -Days 365)

Register-ScheduledTask -TaskName "AutoPinner" -Action $action -Trigger $trigger
```

### Daemon (long-running)

For local/dev only — use Linux cron or Windows Task Scheduler in prod:

```powershell
cd src/AutoPinner
dotnet run -- --daemon
```

## Acceptance / smoke test

1. `dotnet run -- --once` — should print fetch → claim → compose → POST → mark posted, then summary.
2. Re-run immediately — same DesignID should NOT be re-pinned (filtered by the existing pin id check).
3. Temporarily invalidate `PinterestClientSecret` (or move the Pinterest token JSON aside) — failure should be marked `FAILED` on the row, and an alert email should arrive (if configured); a second run inside `ALERT_COOLDOWN_MINUTES` with the same failure should NOT send another email.

## Code layout

```
src/AutoPinner/
├── AutoPinner.csproj                     net8.0; ProjectReference → CrossStitch.Shared
├── Config.cs                             env-var loader (Uploader-aligned naming)
├── DynamoDbDesignRepository.cs           query / claim / mark posted / mark failed
├── Program.cs                            args, lifecycle, batch loop, alerts, retry wrapper
├── EmailNotifier/
│   ├── AlertDeduplicator.cs              fingerprint + cooldown in DDB
│   ├── IEmailNotifier.cs
│   ├── NoopEmailNotifier.cs              logging-only fallback
│   └── SesEmailNotifier.cs               thin wrapper around shared EmailHelper
├── Models/
│   └── Design.cs                         materialised DDB row
└── Utils/
    ├── RateLimiter.cs                    daemon-mode min-interval gate
    └── RetryPolicy.cs                    exponential backoff with jitter (wraps shared uploader)
```

The Pinterest upload / OAuth / SES code itself lives in the shared library — see [`CrossStitch.Shared`](../CrossStitch.Shared/README.md).

## Notes

- **Pin-id attribute drift.** AutoPinner writes the canonical `PinID` attribute (matching Uploader's writer) and reads from all six historical names. See [`dynamodb-schema.md §4.4`](../cross-stitch-platform-docs/docs/integration/dynamodb-schema.md) for the full drift list.
- **Image URL convention.** Defaults to `https://d2o1uvvg91z7o4.cloudfront.net/photos/{AlbumID}/{DesignID}/4.jpg` via `IMAGE_BASE_URL` + `PHOTO_PREFIX`. Same default the cross-stitch reader synthesizes for designs without an explicit `ImageUrl` attribute.
- **Design page URL convention.** Built by the shared `PatternLinkHelper` per [`url-conventions.md §4.1`](../cross-stitch-platform-docs/docs/integration/url-conventions.md).
- **Token refresh.** The shared `PinterestOAuthClient` handles refresh transparently via the JSON token store shared with Uploader (path comes from `cross-stitch-platform-docs/platform-config.json` → `pinterestTokenPath`).
