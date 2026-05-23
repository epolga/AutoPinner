namespace AutoPinner.Models;

/// <summary>
/// Materialised CrossStitchItems row with EntityType="DESIGN", projected
/// to only the fields AutoPinner actually needs.
///
/// Source DDB schema:
///   cross-stitch-platform-docs/docs/integration/dynamodb-schema.md §4.2
/// </summary>
public sealed record Design
{
    public required string Id { get; init; }           // PK, ALB#{AlbumID:D4}
    public required string NPage { get; init; }        // SK, zero-padded 5-digit
    public required int DesignId { get; init; }
    public required int AlbumId { get; init; }
    public required string Caption { get; init; }
    public string Description { get; init; } = "";
    public string Notes { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int NColors { get; init; }

    // The existing Pinterest pin id, if any. Reader-side defensive reading
    // probes six historical attribute names — same logic here so a row that
    // already has a pin under any spelling is treated as "already pinned"
    // and skipped, regardless of which attribute name carries the value.
    public string? PinId { get; init; }

    // Worker-state fields. NEW = never attempted (typical for fresh rows).
    // POSTING = currently locked by some run. POSTED = pinned and PinId set.
    // FAILED = retries exhausted; needs human review.
    public string? PinterestStatus { get; init; }
    public int PinterestAttemptCount { get; init; }
    public string? PinterestLastError { get; init; }
    public string? PinterestLastAttemptAt { get; init; }
}
