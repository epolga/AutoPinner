using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using AutoPinner.Models;

namespace AutoPinner;

/// <summary>
/// Owns all reads/writes against the CrossStitchItems DynamoDB table for the
/// Pinterest-pinning workflow. Three responsibilities:
///   1) Query the DesignsByID-index GSI in descending DesignID order and
///      return the latest N designs that do NOT yet have a pin.
///   2) Atomically claim a design with a conditional UpdateItem so two runs
///      can't pin the same row.
///   3) Stamp final outcome (POSTED + PinID, or FAILED + last error).
///
/// Schema reference: cross-stitch-platform-docs/docs/integration/dynamodb-schema.md §4.2 / §4.4.
/// </summary>
public sealed class DynamoDbDesignRepository : IDisposable
{
    private const string GsiName = "DesignsByID-index";
    private const string Status_Posting = "POSTING";
    private const string Status_Posted = "POSTED";
    private const string Status_Failed = "FAILED";

    // §4.4 documents six historical attribute names for the Pinterest pin id.
    // We treat the design as "already pinned" if any of these is non-empty.
    // The canonical write name is "PinID" (matches Uploader/MainWindow.xaml.cs:1080).
    private static readonly string[] PinIdAttributeNames =
    {
        "PinID",
        "PinId",
        "PinterestPinId",
        "PinterestPinID",
        "PinterestID",
        "PinterestId",
    };
    private const string CanonicalPinIdAttribute = "PinID";

    private readonly AmazonDynamoDBClient _client;
    private readonly string _tableName;

    public DynamoDbDesignRepository(string awsRegion, string tableName)
    {
        _client = new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(awsRegion));
        _tableName = tableName;
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Query the DesignsByID-index GSI in DESC order. Pages until we collect
    /// `take` rows whose pin id is missing under all six historical spellings,
    /// AND whose PinterestStatus is not already POSTING/POSTED. Stops early at
    /// safetyPageLimit pages to avoid runaway scans.
    /// </summary>
    public async Task<IReadOnlyList<Design>> GetLatestUnpinnedAsync(
        int take,
        int safetyPageLimit = 50,
        CancellationToken ct = default)
    {
        var collected = new List<Design>(take);
        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        var page = 0;

        while (collected.Count < take && page < safetyPageLimit)
        {
            page++;
            var req = new QueryRequest
            {
                TableName = _tableName,
                IndexName = GsiName,
                KeyConditionExpression = "#et = :design",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#et"] = "EntityType",
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":design"] = new AttributeValue { S = "DESIGN" },
                },
                ScanIndexForward = false, // DESC by DesignID
                ExclusiveStartKey = exclusiveStartKey,
                Limit = Math.Max(take * 2, 50),
            };

            var resp = await _client.QueryAsync(req, ct).ConfigureAwait(false);

            foreach (var item in resp.Items)
            {
                if (HasAnyPinId(item)) continue;
                if (IsBusyOrPosted(item)) continue;

                var design = ProjectDesign(item);
                if (design is null) continue;
                collected.Add(design);
                if (collected.Count >= take) break;
            }

            exclusiveStartKey = (resp.LastEvaluatedKey is { Count: > 0 }) ? resp.LastEvaluatedKey : null;
            if (exclusiveStartKey is null) break;
        }

        return collected;
    }

    /// <summary>
    /// Attempt to mark a design as POSTING. Returns false if the design has
    /// already been claimed by another run, was completed, or somehow grew a
    /// pin id since we read it. The caller treats false as a non-error skip.
    /// </summary>
    public async Task<bool> TryClaimAsync(Design design, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.ToString("o");
        var updateExpr =
            "SET PinterestStatus = :posting, " +
            "PinterestLastAttemptAt = :now, " +
            "PinterestAttemptCount = if_not_exists(PinterestAttemptCount, :zero) + :one";

        var condition =
            "(attribute_not_exists(PinterestStatus) OR PinterestStatus = :new OR PinterestStatus = :failed) " +
            "AND attribute_not_exists(PinID) AND attribute_not_exists(PinId) " +
            "AND attribute_not_exists(PinterestPinId) AND attribute_not_exists(PinterestPinID) " +
            "AND attribute_not_exists(PinterestID) AND attribute_not_exists(PinterestId)";

        var req = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = PrimaryKey(design),
            UpdateExpression = updateExpr,
            ConditionExpression = condition,
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":posting"] = new AttributeValue { S = Status_Posting },
                [":now"] = new AttributeValue { S = now },
                [":zero"] = new AttributeValue { N = "0" },
                [":one"] = new AttributeValue { N = "1" },
                [":new"] = new AttributeValue { S = "NEW" },
                [":failed"] = new AttributeValue { S = Status_Failed },
            },
        };

        try
        {
            await _client.UpdateItemAsync(req, ct).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stamp POSTED + the returned pin id. Also clears any prior error string.
    /// </summary>
    public async Task MarkPostedAsync(Design design, string pinId, CancellationToken ct = default)
    {
        var req = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = PrimaryKey(design),
            UpdateExpression =
                $"SET {CanonicalPinIdAttribute} = :pinid, PinterestStatus = :posted, PinterestLastAttemptAt = :now " +
                "REMOVE PinterestLastError",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":pinid"] = new AttributeValue { S = pinId },
                [":posted"] = new AttributeValue { S = Status_Posted },
                [":now"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") },
            },
        };
        await _client.UpdateItemAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stamp FAILED with the truncated error message. Preserves attempt count
    /// (already bumped at claim time).
    /// </summary>
    public async Task MarkFailedAsync(Design design, string errorMessage, CancellationToken ct = default)
    {
        var truncated = errorMessage.Length > 500 ? errorMessage[..500] : errorMessage;
        var req = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = PrimaryKey(design),
            UpdateExpression =
                "SET PinterestStatus = :failed, PinterestLastError = :err, PinterestLastAttemptAt = :now",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":failed"] = new AttributeValue { S = Status_Failed },
                [":err"] = new AttributeValue { S = truncated },
                [":now"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") },
            },
        };
        await _client.UpdateItemAsync(req, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, AttributeValue> PrimaryKey(Design design) => new()
    {
        ["ID"] = new AttributeValue { S = design.Id },
        ["NPage"] = new AttributeValue { S = design.NPage },
    };

    private static bool HasAnyPinId(Dictionary<string, AttributeValue> item)
    {
        foreach (var name in PinIdAttributeNames)
        {
            if (item.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v.S))
                return true;
        }
        return false;
    }

    private static bool IsBusyOrPosted(Dictionary<string, AttributeValue> item)
    {
        if (!item.TryGetValue("PinterestStatus", out var v) || string.IsNullOrWhiteSpace(v.S))
            return false;
        return v.S == Status_Posting || v.S == Status_Posted;
    }

    private static Design? ProjectDesign(Dictionary<string, AttributeValue> item)
    {
        if (!item.TryGetValue("ID", out var id) || string.IsNullOrWhiteSpace(id.S)) return null;
        if (!item.TryGetValue("NPage", out var nPage) || string.IsNullOrWhiteSpace(nPage.S)) return null;
        if (!TryReadInt(item, "DesignID", out var designId) || designId <= 0) return null;
        if (!TryReadInt(item, "AlbumID", out var albumId) || albumId <= 0) return null;

        return new Design
        {
            Id = id.S,
            NPage = nPage.S,
            DesignId = designId,
            AlbumId = albumId,
            Caption = ReadString(item, "Caption"),
            Description = ReadString(item, "Description"),
            Notes = ReadString(item, "Notes"),
            Width = TryReadInt(item, "Width", out var w) ? w : 0,
            Height = TryReadInt(item, "Height", out var h) ? h : 0,
            NColors = TryReadInt(item, "NColors", out var c) ? c : 0,
            PinId = ReadFirstPinIdOrNull(item),
            PinterestStatus = ReadString(item, "PinterestStatus"),
            PinterestAttemptCount = TryReadInt(item, "PinterestAttemptCount", out var ac) ? ac : 0,
            PinterestLastError = ReadString(item, "PinterestLastError"),
            PinterestLastAttemptAt = ReadString(item, "PinterestLastAttemptAt"),
        };
    }

    private static string ReadString(Dictionary<string, AttributeValue> item, string name)
        => item.TryGetValue(name, out var v) && v.S is not null ? v.S : "";

    private static bool TryReadInt(Dictionary<string, AttributeValue> item, string name, out int value)
    {
        if (item.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v.N) && int.TryParse(v.N, out value))
            return true;
        value = 0;
        return false;
    }

    private static string? ReadFirstPinIdOrNull(Dictionary<string, AttributeValue> item)
    {
        foreach (var name in PinIdAttributeNames)
        {
            if (item.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v.S))
                return v.S;
        }
        return null;
    }
}
