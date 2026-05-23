using System.Globalization;
using System.Text;
using AutoPinner.Models;

namespace AutoPinner;

/// <summary>
/// Turns a Design row into the title / description / link / image-url payload
/// for the Pinterest v5 create-pin endpoint.
///
/// Title and description templates rotate by DesignID so consecutive pins
/// don't share identical text (anti-spam guard, task spec R3). Hashtag sets
/// rotate independently. Mirrors URL conventions documented at
///   cross-stitch-platform-docs/docs/integration/url-conventions.md §4.1
/// and image URL convention at
///   cross-stitch-platform-docs/docs/integration/dynamodb-schema.md §4.2
///   ("data-access.ts:154 — https://d2o1uvvg91z7o4.cloudfront.net/photos/{AlbumID}/{DesignID}/4.jpg").
/// </summary>
public sealed class PinComposer
{
    private const string ImageBase = "https://d2o1uvvg91z7o4.cloudfront.net";
    private const string DefaultPhotoName = "4.jpg";
    private const int MaxTitleLen = 100;
    private const int MaxDescriptionLen = 500;
    private const int MaxAltTextLen = 500;

    private readonly string _baseUrl;
    private readonly BoardResolver _boards;

    // Title template format strings — {0} = Caption. 4 variants rotated by DesignID%N.
    private static readonly string[] TitleTemplates =
    {
        "{0} Cross Stitch Pattern (PDF)",
        "Free Cross Stitch Pattern: {0}",
        "Cute {0} Cross Stitch Chart – Printable PDF",
        "{0} – Counted Cross Stitch Pattern, Printable PDF",
    };

    // Hashtag rotation sets (each set is appended whole; choice rotates by DesignID%N).
    private static readonly string[][] HashtagSets =
    {
        new[] { "#crossstitch", "#crossstitchpattern", "#embroidery", "#needlework", "#printablepdf" },
        new[] { "#crossstitch", "#countedcrossstitch", "#crossstitchcharts", "#diy", "#handmade" },
        new[] { "#crossstitch", "#embroidery", "#crossstitchkit", "#crossstitchlove", "#stitching" },
    };

    public PinComposer(string baseUrl, BoardResolver boards)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _boards = boards;
    }

    public PinterestCreatePinRequest Compose(Design design)
    {
        var boardId = _boards.Resolve(design.AlbumId);
        var rotation = Math.Abs(design.DesignId);

        var title = BuildTitle(design.Caption, rotation);
        var link = BuildLink(design);
        var description = BuildDescription(design, link, rotation);
        var altText = BuildAltText(design);
        var imageUrl = BuildImageUrl(design.AlbumId, design.DesignId);

        return new PinterestCreatePinRequest
        {
            BoardId = boardId,
            Title = title,
            Description = description,
            AltText = altText,
            Link = link,
            Media = new MediaSource { Url = imageUrl },
        };
    }

    private static string BuildTitle(string caption, int rotation)
    {
        var safeCaption = string.IsNullOrWhiteSpace(caption) ? "Cross Stitch Pattern" : caption.Trim();
        var template = TitleTemplates[rotation % TitleTemplates.Length];
        var title = string.Format(CultureInfo.InvariantCulture, template, safeCaption);
        return title.Length > MaxTitleLen ? title[..MaxTitleLen] : title;
    }

    // URL shape per url-conventions.md §4.1:
    //   /{Caption-with-spaces-as-dashes}-{AlbumID}-{NPage-1}-Free-Design.aspx
    // NPage is a zero-padded 5-digit string in DDB; convert to int and subtract 1.
    private string BuildLink(Design design)
    {
        var slug = (string.IsNullOrWhiteSpace(design.Caption) ? "Design" : design.Caption.Trim())
            .Replace(' ', '-');
        var nPageInt = int.TryParse(design.NPage, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
        var page = Math.Max(0, nPageInt - 1);
        var path = $"/{slug}-{design.AlbumId.ToString(CultureInfo.InvariantCulture)}-{page.ToString(CultureInfo.InvariantCulture)}-Free-Design.aspx";
        return $"{_baseUrl}{path}?utm_source=Pinterest&utm_medium=Organic&utm_campaign=AutoPins";
    }

    private static string BuildImageUrl(int albumId, int designId)
        => $"{ImageBase}/photos/{albumId.ToString(CultureInfo.InvariantCulture)}/{designId.ToString(CultureInfo.InvariantCulture)}/{DefaultPhotoName}";

    private static string BuildDescription(Design design, string link, int rotation)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(design.Caption))
            sb.Append(design.Caption.Trim()).Append(" – ");

        // Required keywords (task spec R3).
        sb.Append("cross stitch pattern. ");

        if (design.Width > 0 && design.Height > 0 && design.NColors > 0)
            sb.Append(CultureInfo.InvariantCulture, $"{design.Width} × {design.Height} stitches, {design.NColors} colours. ");

        if (!string.IsNullOrWhiteSpace(design.Description))
            sb.Append(design.Description.Trim()).Append(' ');

        if (!string.IsNullOrWhiteSpace(design.Notes))
            sb.Append(design.Notes.Trim()).Append(' ');

        sb.Append("Printable PDF download at ").Append(link).Append(". ");

        sb.AppendLine();
        sb.Append(string.Join(' ', HashtagSets[rotation % HashtagSets.Length]));

        var s = sb.ToString();
        return s.Length > MaxDescriptionLen ? s[..MaxDescriptionLen] : s;
    }

    private static string BuildAltText(Design design)
    {
        var parts = new List<string> { "Counted cross stitch pattern" };
        if (!string.IsNullOrWhiteSpace(design.Caption)) parts.Add(design.Caption.Trim());

        var tech = new List<string>();
        if (design.Width > 0 && design.Height > 0) tech.Add($"{design.Width} by {design.Height} stitches");
        if (design.NColors > 0) tech.Add($"{design.NColors} colours");
        if (tech.Count > 0) parts.Add(string.Join(", ", tech));

        var alt = string.Join(", ", parts);
        return alt.Length > MaxAltTextLen ? alt[..MaxAltTextLen] : alt;
    }
}
