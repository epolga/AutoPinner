using System.Globalization;

namespace AutoPinner;

/// <summary>
/// Maps AlbumID → Pinterest BoardID using the AlbumBoards.csv shared with the
/// Uploader project (Uploader/Uploader/AlbumBoards.csv, mirrored here).
///
/// CSV shape (Uploader/Helpers/PinterestHelper.cs:184-220 is the authoritative
/// implementation we're mirroring):
///     AlbumID,AlbumCaption,BoardID
///     0104,"Cushion Covers",257127528664615685
///     ...
/// AlbumID is zero-padded to 4 digits. Caption may be quoted and contain commas.
///
/// If the album isn't in the CSV, falls back to DefaultBoardId from Config; if
/// that's also missing, throws — the caller treats this as a non-transient
/// failure (no point retrying without a board to post to).
/// </summary>
public sealed class BoardResolver
{
    private readonly Dictionary<string, string> _byAlbumIdKey;
    private readonly string? _defaultBoardId;
    private readonly string _csvPath;

    private BoardResolver(Dictionary<string, string> byAlbumIdKey, string? defaultBoardId, string csvPath)
    {
        _byAlbumIdKey = byAlbumIdKey;
        _defaultBoardId = defaultBoardId;
        _csvPath = csvPath;
    }

    public static async Task<BoardResolver> LoadAsync(string csvPath, string? defaultBoardId, CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(csvPath))
        {
            var lines = await File.ReadAllLinesAsync(csvPath, ct).ConfigureAwait(false);
            for (var i = 1; i < lines.Length; i++) // skip header
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!TryParse(line, out var albumKey, out var boardId)) continue;
                map.TryAdd(albumKey, boardId);
            }
        }

        if (map.Count == 0 && string.IsNullOrWhiteSpace(defaultBoardId))
            Console.Error.WriteLine(
                $"WARN: BoardResolver found 0 entries in '{csvPath}' and no DEFAULT_BOARD_ID is set. " +
                "Every pin attempt will fail until either is populated.");

        return new BoardResolver(map, defaultBoardId, csvPath);
    }

    public string Resolve(int albumId)
    {
        var key = albumId.ToString("D4", CultureInfo.InvariantCulture);
        if (_byAlbumIdKey.TryGetValue(key, out var boardId) && !string.IsNullOrWhiteSpace(boardId))
            return boardId;
        if (!string.IsNullOrWhiteSpace(_defaultBoardId))
            return _defaultBoardId;
        throw new InvalidOperationException(
            $"No board configured for AlbumID={albumId} (key {key}). Check {_csvPath} or set DEFAULT_BOARD_ID.");
    }

    public int MappedCount => _byAlbumIdKey.Count;

    // Mirrors PinterestHelper.cs:TryParseAlbumBoardsCsvLine so the two stay
    // bug-compatible: first token before first comma is AlbumID, everything
    // after the last comma is BoardID (caption can contain commas if quoted).
    private static bool TryParse(string line, out string albumId, out string boardId)
    {
        albumId = string.Empty;
        boardId = string.Empty;

        var firstComma = line.IndexOf(',');
        var lastComma = line.LastIndexOf(',');
        if (firstComma <= 0 || lastComma <= firstComma) return false;

        albumId = line[..firstComma].Trim();
        var raw = line[(lastComma + 1)..].Trim();
        if (raw.Length >= 2 && raw.StartsWith('"') && raw.EndsWith('"'))
            raw = raw[1..^1];
        boardId = raw;

        return albumId.Length > 0 && boardId.Length > 0;
    }
}
