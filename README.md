# AutoPinner

C# .NET 8 worker that pulls cross-stitch designs from DynamoDB (newest first), creates Pinterest pins for the ones that don't have a pin yet, and writes the returned pin id back into the same row. Runs as a one-shot (`--once`) or a long-running daemon (`--daemon`).

Sister project of [`Uploader`](../Uploader) (which writes new designs into DynamoDB after upload) and [`cross-stitch/automation/pinterest-agent`](../cross-stitch/automation/pinterest-agent) (the daily analytics/reporting agent). Lives in the same workspace.

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

Reads `AlbumBoards.csv` (`AlbumID,AlbumCaption,BoardID` — 4-digit zero-padded AlbumID), matching the format produced by [`Uploader/Helpers/PinterestBoardCreator.cs`](../Uploader/Uploader/Helpers/PinterestBoardCreator.cs) and consumed by [`Uploader/Helpers/PinterestHelper.cs`](../Uploader/Uploader/Helpers/PinterestHelper.cs). The CSV is mirrored into this repo at [`src/AutoPinner/AlbumBoards.csv`](src/AutoPinner/AlbumBoards.csv); sync it manually if Uploader's diverges.

If an album isn't in the CSV, falls back to `DEFAULT_BOARD_ID`; if that's also unset, throws (no board → no pin).

## Email alerts

When a non-transient API error, persistent DDB failure, or `N` consecutive failures occurs, AutoPinner emails the operator. Defaults:

- Transport: AWS SES (`AUTO_PINNER_EMAIL_TRANSPORT=ses`). SMTP fallback (`=smtp`) supported via `SES_SMTP_*` vars.
- Dedup: an SHA-256 fingerprint of `operation|status|errorClass` is stored at DDB `ID=SYS#ALERTS, NPage=AUTOPINNER` along with `LastAlertAtUtc`. Same-fingerprint alerts within `ALERT_COOLDOWN_MINUTES` (default 60) are suppressed.
- Threshold: `ALERT_CONSECUTIVE_FAILURE_THRESHOLD` (default 5) triggers a separate "still failing" alert.
- If both `ALERT_EMAIL_TO` and `ALERT_EMAIL_FROM` are empty, the notifier is a no-op (failures are still logged to stderr).

The sender identity must be verified in SES. The cross-stitch workspace already has `ann@cross-stitch.com` verified (used by Uploader and the pinterest-agent); reuse it by setting `ALERT_EMAIL_FROM=ann@cross-stitch.com`.

## Configuration

All settings come from environment variables. AutoPinner auto-loads a `.env` file at startup (via `DotNetEnv`) — copy [`.env.example`](.env.example) to `.env` and fill in real values.

| Variable | Default | Purpose |
|---|---|---|
| `AWS_REGION` | `us-east-1` | DDB + SES region |
| `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` (or `AWS_PROFILE`) | — | AWS credentials. The pinterest-agent IAM user has the required `dynamodb:Query/UpdateItem/PutItem/GetItem` on `CrossStitchItems` and `ses:SendEmail` on the cross-stitch.com identity. |
| `DDB_TABLE_NAME` | `CrossStitchItems` | Table name |
| `PINTEREST_ACCESS_TOKEN` | required | Pinterest v5 OAuth bearer token |
| `POST_INTERVAL_SECONDS` | `300` | Min gap between pins in daemon mode |
| `DAILY_CAP` | `200` | Max pins per process lifetime |
| `MAX_BATCH_PER_RUN` | `1` | Batch size per `--once` invocation |
| `BASE_URL` | `https://cross-stitch.com` | Used to build pin destination links |
| `ENVIRONMENT_NAME` | `dev` | Appears in email subjects |
| `BOARDS_CSV_PATH` | `AlbumBoards.csv` | AlbumID → BoardID mapping |
| `DEFAULT_BOARD_ID` | — | Fallback board if album not in CSV |
| `ALERT_EMAIL_TO` / `ALERT_EMAIL_FROM` | — | Operator recipient + verified SES sender. Both empty disables email. |
| `ALERT_COOLDOWN_MINUTES` | `60` | Dedup window for same-fingerprint alerts |
| `ALERT_CONSECUTIVE_FAILURE_THRESHOLD` | `5` | Trigger a "still failing" alert at this run-streak |
| `ALERT_DAILY_SUMMARY_ENABLED` | `false` | (Planned) daily roll-up email |
| `ALERT_DAILY_SUMMARY_HOUR_UTC` | `7` | (Planned) hour at which the daily summary fires |
| `SES_CONFIGURATION_SET` | — | Optional SES configuration set |
| `AUTO_PINNER_EMAIL_TRANSPORT` | `ses` | `ses` or `smtp` |
| `SES_SMTP_HOST` / `SES_SMTP_PORT` / `SES_SMTP_USER` / `SES_SMTP_PASS` | — | Required only if transport=smtp |

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
3. Temporarily invalidate `PINTEREST_ACCESS_TOKEN` — failure should be marked `FAILED` on the row, and an alert email should arrive (if configured); a second run inside `ALERT_COOLDOWN_MINUTES` with the same failure should NOT send another email.

## Code layout

```
src/AutoPinner/
├── AlbumBoards.csv                       (mirrored from Uploader; sync manually)
├── AutoPinner.csproj
├── BoardResolver.cs                      AlbumID → BoardID lookup
├── Config.cs                             env-var loader
├── DynamoDbDesignRepository.cs           query / claim / mark posted / mark failed
├── PinComposer.cs                        title / description / link / image URL builder
├── PinterestClient.cs                    v5 create-pin with backoff
├── Program.cs                            args, lifecycle, batch loop, alerts
├── EmailNotifier/
│   ├── AlertDeduplicator.cs              fingerprint + cooldown in DDB
│   ├── IEmailNotifier.cs
│   ├── NoopEmailNotifier.cs              logging-only fallback
│   ├── SesEmailNotifier.cs               AWS SDK SES API
│   └── SmtpEmailNotifier.cs              SES SMTP relay or any other SMTP
├── Models/
│   ├── Design.cs                         materialised DDB row
│   ├── PinterestCreatePinRequest.cs
│   └── PinterestCreatePinResponse.cs
└── Utils/
    ├── RateLimiter.cs                    daemon-mode min-interval gate
    └── RetryPolicy.cs                    exponential backoff with jitter
```

## Notes

- **Pin-id attribute drift.** AutoPinner writes the canonical `PinID` attribute (matching Uploader's writer) and reads from all six historical names. See [`dynamodb-schema.md §4.4`](../cross-stitch-platform-docs/docs/integration/dynamodb-schema.md) for the full drift list.
- **Image URL convention.** Pulls from `https://d2o1uvvg91z7o4.cloudfront.net/photos/{AlbumID}/{DesignID}/4.jpg` (the same default the cross-stitch reader synthesizes for designs without an explicit `ImageUrl` attribute).
- **Design page URL convention.** Builds `{BASE_URL}/{Caption-with-spaces-as-dashes}-{AlbumID}-{NPage-1}-Free-Design.aspx?utm_source=Pinterest&utm_medium=Organic&utm_campaign=AutoPins` per [`url-conventions.md §4.1`](../cross-stitch-platform-docs/docs/integration/url-conventions.md).
- **Token refresh.** v1 uses a static `PINTEREST_ACCESS_TOKEN` env var. If Pinterest tokens expire in your usage pattern, plumb in a refresh-token flow (Uploader has `PinterestOAuthClient` you can port).
