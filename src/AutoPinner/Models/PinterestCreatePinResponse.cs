using System.Text.Json.Serialization;

namespace AutoPinner.Models;

/// <summary>
/// Subset of the Pinterest v5 Create Pin response we actually consume.
/// `id` is the only field we care about (stored back into DynamoDB as PinID).
/// </summary>
public sealed class PinterestCreatePinResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
