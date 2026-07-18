using System.Collections.Generic;

namespace pdf_studio.Models;

/// <summary>A rectangle in PDF user-space coordinates (points).</summary>
public readonly record struct PdfRect(double L, double T, double R, double B);

/// <summary>
/// A persistent highlight annotation on a single PDF page.
/// Rects are line-level rectangles in PDF coordinates.
/// </summary>
public sealed class PdfHighlight
{
    public int PageIndex { get; }
    public List<PdfRect> Rects { get; }
    public Windows.UI.Color Color { get; set; }

    public PdfHighlight(int pageIndex, List<PdfRect> rects, Windows.UI.Color color)
    {
        PageIndex = pageIndex;
        Rects = rects;
        Color = color;
    }
}

/// <summary>A node of the document outline (bookmarks) tree.</summary>
public sealed class TocItem
{
    public string Title { get; set; } = string.Empty;

    /// <summary>Zero-based destination page index, or -1 when unavailable.</summary>
    public int PageIndex { get; set; } = -1;

    public List<TocItem> Children { get; } = [];

    // TreeViewNode displays Content.ToString() when no template is set.
    public override string ToString() => Title;
}
