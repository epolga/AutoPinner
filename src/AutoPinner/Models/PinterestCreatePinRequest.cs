using System.Text.Json.Serialization;

namespace AutoPinner.Models;

/// <summary>
/// Body for POST https://api.pinterest.com/v5/pins.
/// Field names match the v5 API exactly (snake_case via JsonPropertyName).
/// </summary>
public sealed class PinterestCreatePinRequest
{
    [JsonPropertyName("board_id")]
    public required string BoardId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("link")]
    public required string Link { get; init; }

    [JsonPropertyName("alt_text")]
    public string? AltText { get; init; }

    [JsonPropertyName("media_source")]
    public required MediaSource Media { get; init; }
}

public sealed class MediaSource
{
    [JsonPropertyName("source_type")]
    public string SourceType { get; init; } = "image_url";

    [JsonPropertyName("url")]
    public required string Url { get; init; }
}
