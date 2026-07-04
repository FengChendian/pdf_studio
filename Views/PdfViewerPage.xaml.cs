using System;
using System.ComponentModel;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using pdf_studio.Services;
using PDFiumCore;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace pdf_studio.Views;

public sealed partial class PdfViewerPage : Page
{
    public readonly string FileName;

    public ObservableCollection<WriteableBitmap> Images { get; private set; } = [];
    public readonly ImagesVirtualizingCollection VirtualizingImages;

    private DispatcherTimer? _highlightRedrawTimer;

    public int RenderedPageCount
    {
        get => VirtualizingImages.Count;
    }

    public PdfViewerPage(string filename)
    {
        FileName = filename;
        VirtualizingImages = new ImagesVirtualizingCollection(FileName);
        InitializeComponent();
        VirtualizingImages.CollectionChanged += OnVirtualizingImagesCollectionChanged;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        FileNameLabel.Text = FileName;

        // Render all placeholder images (parallel, scale=1)
        await VirtualizingImages.InitializePlaceholdersAsync();

        // Switch visibility
        LoadingOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        PdfScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private double _lastAvailableWidth = -1;

    private void PdfScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double availableWidth = e.NewSize.Width - 64; // ItemsRepeater margin (32 * 2)
        if (availableWidth > 0 && Math.Abs(availableWidth - _lastAvailableWidth) > 1.0)
        {
            _lastAvailableWidth = availableWidth;
            VirtualizingImages.CalculateDisplaySizes(availableWidth);

            if (_selectionStartPage >= 0)
                DrawSelectionHighlights();
        }
    }

    private void PdfScrollViewer_ViewChanged(ScrollView sender, object args)
    {
        // Debounce a redraw of the selection highlights so scrolling/zooming
        // does not leave stale or missing highlights on newly realized pages.
        if (_selectionStartPage < 0) return;

        if (_highlightRedrawTimer == null)
        {
            _highlightRedrawTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _highlightRedrawTimer.Tick += (_, _) =>
            {
                _highlightRedrawTimer?.Stop();
                DrawSelectionHighlights();
            };
        }

        _highlightRedrawTimer.Stop();
        _highlightRedrawTimer.Start();
    }

    /// <summary>
    /// Redraws selection highlights when a realized page is replaced (e.g.
    /// high-res swap) if the page falls inside the current selection.
    /// </summary>
    private void OnVirtualizingImagesCollectionChanged(
        object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_selectionStartPage < 0 || _selectionEndPage < 0)
            return;

        bool needsRedraw = false;

        if (e.Action == NotifyCollectionChangedAction.Replace &&
            e.NewItems?.Count > 0 &&
            e.NewItems[0] is PageImageItem item)
        {
            int from = Math.Min(_selectionStartPage, _selectionEndPage);
            int to = Math.Max(_selectionStartPage, _selectionEndPage);
            needsRedraw = item.PageIndex >= from && item.PageIndex <= to;
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            needsRedraw = true;
        }

        if (needsRedraw)
            DrawSelectionHighlights();
    }

    // ════════════════════════════════════════════════════════════════
    //  Text selection state
    // ════════════════════════════════════════════════════════════════

    private bool _isSelecting;
    // Cross-page selection: (startPage, startChar) → (endPage, endChar).
    // Pages between start and end are fully selected.
    private int _selectionStartPage = -1;
    private int _selectionEndPage = -1;
    private int _selectionStartChar = -1;
    private int _selectionEndChar = -1;

    // Cursor-tracking state (throttled to avoid per-pixel text-page queries).
    // Compared in PDF coordinates, so we never need GetCurrentPoint(ScrollView)
    // during PointerMoved — eliminating ScrollView‑internal state flushes.
    private Point _lastCursorCheckPdfPoint;
    private int _lastCursorCheckPage = -1;

    /// <summary>Highlight colour: semi-transparent blue (#0078D7 @ 40%).</summary>
    private static readonly Windows.UI.Color HighlightColor =
        Windows.UI.Color.FromArgb(100, 0, 120, 215);

    // ════════════════════════════════════════════════════════════════
    //  Pointer event handlers (wired in XAML)
    // ════════════════════════════════════════════════════════════════

    private void PdfScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ClearSelection();

        var (item, pdfPoint) = HitTestPageImage(e);
        if (item == null || pdfPoint == null) return;

        var textPage = LoadTextPageForSelection(item.PageIndex);
        if (textPage == null) return;

        int charIndex = PDFiumService.TextGetCharIndexAtPos(
            textPage, pdfPoint.Value.X, pdfPoint.Value.Y, 10, 10);
        if (charIndex < 0) return;

        _isSelecting = true;
        _selectionStartPage = item.PageIndex;
        _selectionEndPage = item.PageIndex;
        _selectionStartChar = charIndex;
        _selectionEndChar = charIndex;

        e.Handled = true;
    }

    private void PdfScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSelecting)
        {
            UpdateTextCursor(e);
            return;
        }

        var (item, pdfPoint) = HitTestPageImage(e);
        if (item == null || pdfPoint == null) return;

        int curPage = item.PageIndex;
        var curTextPage = LoadTextPageForSelection(curPage);
        if (curTextPage == null) return;

        int charIndex = PDFiumService.TextGetCharIndexAtPos(
            curTextPage, pdfPoint.Value.X, pdfPoint.Value.Y, 10, 10);
        if (charIndex < 0) return;

        if (curPage != _selectionEndPage || charIndex != _selectionEndChar)
        {
            _selectionEndPage = curPage;
            _selectionEndChar = charIndex;
            DrawSelectionHighlights();
        }

        e.Handled = true;
    }

    private void PdfScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSelecting) return;

        CopySelectionToClipboard();
        // Keep highlights visible until the next PointerPressed clears them.
        _isSelecting = false;
        e.Handled = true;
    }

    private void PdfScrollViewer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = null;
        _lastCursorCheckPage = -1;
        _lastCursorCheckPdfPoint = default;
    }

    // ════════════════════════════════════════════════════════════════
    //  Hit-testing & coordinate mapping
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Hit-tests the pointer against the realized page images and returns the
    /// matching <see cref="PageImageItem"/> together with the corresponding
    /// PDF user-space coordinate.
    /// </summary>
    /// <remarks>
    /// This method uses the actual visual-tree positions of the page images
    /// instead of reconstructing the ItemsRepeater layout manually, so it stays
    /// aligned with the rendered page after zoom, resize, and high-res replacement.
    /// </remarks>
    private (PageImageItem? Item, Point? PdfPoint) HitTestPageImage(
        PointerRoutedEventArgs e)
    {
        var repeaterPoint = e.GetCurrentPoint(PagesRepeater).Position;

        for (int i = 0; i < VirtualizingImages.Count; i++)
        {
            var image = GetPageImageElement(i);
            if (image == null)
                continue;

            if (image.DataContext is not PageImageItem item)
                continue;

            if (image.ActualWidth <= 0 || image.ActualHeight <= 0)
                continue;

            var bounds = image.TransformToVisual(PagesRepeater)
                .TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight));

            if (bounds.Contains(repeaterPoint))
            {
                var pdfPoint = MapRepeaterPointToPdf(repeaterPoint, image, item.PageIndex);
                if (pdfPoint.HasValue)
                    return (item, pdfPoint.Value);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Returns the realized Image element for a page index, or null if the
    /// ItemsRepeater has not materialized the container for that page.
    /// </summary>
    private Image? GetPageImageElement(int pageIndex)
    {
        var element = PagesRepeater.TryGetElement(pageIndex);
        if (element is Border border && border.Child is Image image)
            return image;

        return null;
    }

    /// <summary>
    /// Returns the rectangle (in the Image element's own coordinate space)
    /// that the bitmap actually occupies when stretched with Stretch=Uniform.
    /// </summary>
    private static Rect GetDisplayedBitmapRect(Image image, WriteableBitmap bitmap)
    {
        double imgW = image.ActualWidth;
        double imgH = image.ActualHeight;
        double bmpW = bitmap.PixelWidth;
        double bmpH = bitmap.PixelHeight;

        if (imgW <= 0 || imgH <= 0 || bmpW <= 0 || bmpH <= 0)
            return Rect.Empty;

        double scale = Math.Min(imgW / bmpW, imgH / bmpH);
        double displayedW = bmpW * scale;
        double displayedH = bmpH * scale;
        double offsetX = (imgW - displayedW) / 2.0;
        double offsetY = (imgH - displayedH) / 2.0;

        return new Rect(offsetX, offsetY, displayedW, displayedH);
    }

    /// <summary>
    /// Maps a pointer position expressed in PagesRepeater coordinates to PDF
    /// user-space coordinates for the page represented by the given Image.
    /// </summary>
    private Point? MapRepeaterPointToPdf(Point pointInRepeater, Image image, int pageIndex)
    {
        if (image.ActualWidth <= 0 || image.ActualHeight <= 0)
            return null;

        if (image.Source is not WriteableBitmap bitmap)
            return null;

        var imageBoundsInRepeater = image.TransformToVisual(PagesRepeater)
            .TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight));

        double localX = pointInRepeater.X - imageBoundsInRepeater.X;
        double localY = pointInRepeater.Y - imageBoundsInRepeater.Y;

        var bitmapRect = GetDisplayedBitmapRect(image, bitmap);
        if (bitmapRect.IsEmpty)
            return null;

        localX = Math.Clamp(localX - bitmapRect.X, 0, bitmapRect.Width);
        localY = Math.Clamp(localY - bitmapRect.Y, 0, bitmapRect.Height);

        var (pageW, pageH) = VirtualizingImages.GetPageSizeInPoints(pageIndex);
        var (originLeft, originBottom) = VirtualizingImages.GetPageOrigin(pageIndex);

        double pdfX = originLeft + localX * (pageW / bitmapRect.Width);
        double pdfY = originBottom + pageH - localY * (pageH / bitmapRect.Height);

        return new Point(pdfX, pdfY);
    }

    /// <summary>
    /// Maps a single PDF text rectangle to a bounding rectangle on the
    /// HighlightOverlay Canvas.
    /// </summary>
    private Rect MapPdfRectToOverlayRect(
        (double L, double T, double R, double B) pdfRect,
        Image image,
        int pageIndex)
    {
        if (image.Source is not WriteableBitmap bitmap)
            return Rect.Empty;

        var bitmapRect = GetDisplayedBitmapRect(image, bitmap);
        if (bitmapRect.IsEmpty)
            return Rect.Empty;

        var (pageW, pageH) = VirtualizingImages.GetPageSizeInPoints(pageIndex);
        var (originLeft, originBottom) = VirtualizingImages.GetPageOrigin(pageIndex);

        double scaleX = bitmapRect.Width / pageW;
        double scaleY = bitmapRect.Height / pageH;

        double localX = bitmapRect.X + (pdfRect.L - originLeft) * scaleX;
        double localY = bitmapRect.Y + (originBottom + pageH - pdfRect.T) * scaleY;
        double localW = (pdfRect.R - pdfRect.L) * scaleX;
        double localH = (pdfRect.T - pdfRect.B) * scaleY;

        var transform = image.TransformToVisual(HighlightOverlay);
        return transform.TransformBounds(new Rect(localX, localY, localW, localH));
    }

    // ════════════════════════════════════════════════════════════════
    //  Text cursor (I‑beam when hovering over text)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called on every PointerMoved when <em>not</em> in selection mode.
    /// Throttled: re-checks only when the pointer moves ≥ 5 DIPs or
    /// crosses to a different page.
    /// </summary>
    private void UpdateTextCursor(PointerRoutedEventArgs e)
    {
        // HitTestPageImage is side‑effect‑free — no visual‑tree access, no
        // render enqueue.  We get the PDF point back and throttle on that,
        // avoiding e.GetCurrentPoint(ScrollView) which triggers ScrollView
        // internal state flushes during PointerMoved.
        var (item, pdfPoint) = HitTestPageImage(e);
        if (item == null || pdfPoint == null)
        {
            ProtectedCursor = null;
            _lastCursorCheckPage = -1;
            return;
        }

        // Same page + tiny movement in PDF space → skip PDFium re-query.
        if (item.PageIndex == _lastCursorCheckPage)
        {
            double dx = Math.Abs(pdfPoint.Value.X - _lastCursorCheckPdfPoint.X);
            double dy = Math.Abs(pdfPoint.Value.Y - _lastCursorCheckPdfPoint.Y);
            if (dx < 5 && dy < 5)
                return;
        }

        _lastCursorCheckPdfPoint = pdfPoint.Value;
        _lastCursorCheckPage = item.PageIndex;

        var textPage = LoadTextPageForSelection(item.PageIndex);
        if (textPage == null)
        {
            ProtectedCursor = null;
            return;
        }

        int charIndex = PDFiumService.TextGetCharIndexAtPos(
            textPage, pdfPoint.Value.X, pdfPoint.Value.Y, 3, 3);

        ProtectedCursor = charIndex >= 0
            ? InputSystemCursor.Create(InputSystemCursorShape.IBeam)
            : null;
    }

    // ════════════════════════════════════════════════════════════════
    //  Text page loading
    // ════════════════════════════════════════════════════════════════

    private FpdfTextpageT? LoadTextPageForSelection(int pageIndex)
    {
        try
        {
            return VirtualizingImages.PdfiumService.LoadTextPage(
                VirtualizingImages.FilePath, pageIndex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextLayer] Failed to load text page {pageIndex}: {ex.Message}");
            return null;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Highlight drawing (cross-page)
    // ════════════════════════════════════════════════════════════════

    private void DrawSelectionHighlights()
    {
        HighlightOverlay.Children.Clear();

        if (_selectionStartPage < 0 || _selectionEndPage < 0) return;

        // Normalise page range.
        int pageFrom = Math.Min(_selectionStartPage, _selectionEndPage);
        int pageTo = Math.Max(_selectionStartPage, _selectionEndPage);
        int charFrom, charTo;

        if (_selectionStartPage < _selectionEndPage)
        {
            charFrom = _selectionStartChar;
            charTo = _selectionEndChar;
        }
        else if (_selectionStartPage > _selectionEndPage)
        {
            charFrom = _selectionEndChar;
            charTo = _selectionStartChar;
        }
        else
        {
            charFrom = Math.Min(_selectionStartChar, _selectionEndChar);
            charTo = Math.Max(_selectionStartChar, _selectionEndChar);
        }

        if (charTo <= charFrom && pageFrom == pageTo) return;

        // Draw highlights for each page in the range.
        for (int p = pageFrom; p <= pageTo; p++)
        {
            var textPage = LoadTextPageForSelection(p);
            if (textPage == null) continue;

            int startChar, charCount;
            if (p == pageFrom && p == pageTo)
            {
                startChar = charFrom;
                charCount = charTo - charFrom;
            }
            else if (p == pageFrom)
            {
                int total = PDFiumService.TextCountChars(textPage);
                startChar = charFrom;
                charCount = total - charFrom;
            }
            else if (p == pageTo)
            {
                startChar = 0;
                charCount = charTo;
            }
            else
            {
                startChar = 0;
                charCount = PDFiumService.TextCountChars(textPage);
            }

            if (charCount <= 0) continue;
            DrawPageHighlights(p, textPage, startChar, charCount);
        }
    }

    /// <summary>
    /// Draws highlight rectangles for a single page on the overlay Canvas.
    /// Merges adjacent text runs on the same line into continuous blocks.
    /// </summary>
    private void DrawPageHighlights(int pageIndex, FpdfTextpageT textPage,
        int startChar, int charCount)
    {
        var image = GetPageImageElement(pageIndex);
        if (image == null) return;

        // ── 1. Collect raw rects from PDFium (PDF coordinates) ──────
        int rectCount = PDFiumService.TextCountRects(textPage, startChar, charCount);
        if (rectCount <= 0) return;

        var rawRects = new List<(double L, double T, double R, double B)>(rectCount);
        for (int i = 0; i < rectCount; i++)
        {
            if (PDFiumService.TextGetRect(textPage, i,
                    out double l, out double t, out double r, out double b))
                rawRects.Add((l, t, r, b));
        }
        if (rawRects.Count == 0) return;

        // ── 2. Merge rects that belong to the same visual line ──────
        var merged = MergeRectsByLine(rawRects);
        if (merged.Count == 0) return;

        // ── 3. Draw merged rects using the actual visual position ────
        foreach (var rect in merged)
        {
            var overlayRect = MapPdfRectToOverlayRect(rect, image, pageIndex);
            if (overlayRect.IsEmpty || overlayRect.Width <= 0 || overlayRect.Height <= 0)
                continue;

            var highlight = new Rectangle
            {
                Fill = new SolidColorBrush(HighlightColor),
                Width = overlayRect.Width,
                Height = overlayRect.Height,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(highlight, overlayRect.X);
            Canvas.SetTop(highlight, overlayRect.Y);
            HighlightOverlay.Children.Add(highlight);
        }
    }

    /// <summary>
    /// Merges a collection of PDF text rectangles into line-level
    /// continuous blocks.  Groups by vertical overlap (same visual
    /// line), then merges horizontally with a gap threshold
    /// proportional to the line's character height.  The threshold
    /// (2× average char height, min 8 pt) is wide enough to cover
    /// word spaces (~3 pt) but far too narrow to cross column gaps
    /// (typically 200+ pt).
    /// </summary>
    private static List<(double L, double T, double R, double B)> MergeRectsByLine(
        List<(double L, double T, double R, double B)> rects)
    {
        // ── 1. Group by visual line (vertical overlap) ────────────
        rects.Sort((a, b) => b.T.CompareTo(a.T)); // top → bottom

        var lines = new List<List<(double L, double T, double R, double B)>>();
        foreach (var rect in rects)
        {
            bool added = false;
            foreach (var line in lines)
            {
                double lineTop = double.MinValue, lineBot = double.MaxValue;
                foreach (var lr in line)
                {
                    if (lr.T > lineTop) lineTop = lr.T;
                    if (lr.B < lineBot) lineBot = lr.B;
                }
                if (rect.T > lineBot && rect.B < lineTop)
                {
                    line.Add(rect);
                    added = true;
                    break;
                }
            }
            if (!added)
                lines.Add(new List<(double, double, double, double)> { rect });
        }

        // ── 2. Merge horizontally with proportional gap ──────────
        var result = new List<(double L, double T, double R, double B)>();
        foreach (var line in lines)
        {
            line.Sort((a, b) => a.L.CompareTo(b.L)); // left → right

            double avgCharH = line.Average(r => r.T - r.B);
            double maxGap = Math.Max(avgCharH * 2.0, 8.0);

            double curL = line[0].L, curR = line[0].R;
            double curT = line[0].T, curB = line[0].B;

            for (int i = 1; i < line.Count; i++)
            {
                var (l, t, r, b) = line[i];
                if (l <= curR + maxGap)
                {
                    if (r > curR) curR = r;
                    if (t > curT) curT = t;
                    if (b < curB) curB = b;
                }
                else
                {
                    result.Add((curL, curT, curR, curB));
                    curL = l; curR = r; curT = t; curB = b;
                }
            }
            result.Add((curL, curT, curR, curB));
        }

        return result;
    }

    private void ClearSelection()
    {
        _isSelecting = false;
        _selectionStartPage = -1;
        _selectionEndPage = -1;
        _selectionStartChar = -1;
        _selectionEndChar = -1;
        HighlightOverlay.Children.Clear();
    }

    // ════════════════════════════════════════════════════════════════
    //  Clipboard (cross-page)
    // ════════════════════════════════════════════════════════════════

    private void CopySelectionToClipboard()
    {
        if (_selectionStartPage < 0 || _selectionEndPage < 0) return;

        int pageFrom = Math.Min(_selectionStartPage, _selectionEndPage);
        int pageTo = Math.Max(_selectionStartPage, _selectionEndPage);
        int charFrom, charTo;

        if (_selectionStartPage < _selectionEndPage)
        {
            charFrom = _selectionStartChar;
            charTo = _selectionEndChar;
        }
        else if (_selectionStartPage > _selectionEndPage)
        {
            charFrom = _selectionEndChar;
            charTo = _selectionStartChar;
        }
        else
        {
            charFrom = Math.Min(_selectionStartChar, _selectionEndChar);
            charTo = Math.Max(_selectionStartChar, _selectionEndChar);
        }

        if (charTo <= charFrom && pageFrom == pageTo) return;

        var sb = new System.Text.StringBuilder();
        for (int p = pageFrom; p <= pageTo; p++)
        {
            var textPage = LoadTextPageForSelection(p);
            if (textPage == null) continue;

            int startChar, charCount;
            if (p == pageFrom && p == pageTo)
            {
                startChar = charFrom;
                charCount = charTo - charFrom;
            }
            else if (p == pageFrom)
            {
                int total = PDFiumService.TextCountChars(textPage);
                startChar = charFrom;
                charCount = total - charFrom;
            }
            else if (p == pageTo)
            {
                startChar = 0;
                charCount = charTo;
            }
            else
            {
                startChar = 0;
                charCount = PDFiumService.TextCountChars(textPage);
            }

            if (charCount <= 0) continue;

            string text = PDFiumService.TextGetText(textPage, startChar, charCount);
            if (!string.IsNullOrEmpty(text))
            {
                if (sb.Length > 0 && p > pageFrom)
                    sb.AppendLine();
                sb.Append(text);
            }
        }

        if (sb.Length == 0) return;

        var dataPackage = new DataPackage();
        dataPackage.SetText(sb.ToString());
        Clipboard.SetContent(dataPackage);
        Debug.WriteLine($"[TextLayer] Copied: {sb.Length} chars across {pageTo - pageFrom + 1} page(s)");
    }
}

// ════════════════════════════════════════════════════════════════
//  Data item for ItemsRepeater
// ════════════════════════════════════════════════════════════════

public class PageImageItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private WriteableBitmap? _image;
    private double _width = double.NaN;
    private double _height = double.NaN;
    private double _containerWidth = double.NaN;

    /// <summary>
    /// Rendered page bitmap. <see cref="WriteableBitmap"/> is used directly
    /// as <see cref="Image.Source"/> — no PNG round-trip needed.
    /// </summary>
    public WriteableBitmap? Image
    {
        get => _image;
        set { if (_image != value) { _image = value; PropertyChanged?.Invoke(this, new(nameof(Image))); } }
    }

    public double Width
    {
        get => _width;
        set { if (_width != value) { _width = value; PropertyChanged?.Invoke(this, new(nameof(Width))); } }
    }

    public double Height
    {
        get => _height;
        set { if (_height != value) { _height = value; PropertyChanged?.Invoke(this, new(nameof(Height))); } }
    }

    public double ContainerWidth
    {
        get => _containerWidth;
        set { if (_containerWidth != value) { _containerWidth = value; PropertyChanged?.Invoke(this, new(nameof(ContainerWidth))); } }
    }

    /// <summary>Zero-based page index within the document.</summary>
    public int PageIndex { get; set; }

    /// <summary>
    /// Highlight rectangles for the current text selection on this page
    /// (in WinUI coordinates, relative to the Image element).
    /// </summary>
    public ObservableCollection<HighlightRect> HighlightRects { get; set; } = [];
}

/// <summary>
/// A highlight rectangle for XAML Canvas binding.
/// </summary>
public class HighlightRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

// ════════════════════════════════════════════════════════════════
//  Virtualizing collection backed by PDFiumService
// ════════════════════════════════════════════════════════════════

public partial class ImagesVirtualizingCollection : IList, INotifyCollectionChanged, IDisposable
{
    private readonly Dictionary<int, PageImageItem> _placeHolderCache = [];
    private readonly LinkedList<KeyValuePair<int, PageImageItem>> _highResCache = new();
    private readonly object _cacheLock = new();
    private const int MaxHighResCacheSize = 5;
    private volatile int _currentRenderDpi = 400;
    private readonly Dictionary<int, (double Width, double Height)> _pageSizes = [];
    private readonly Dictionary<int, (double Left, double Bottom)> _pageOrigins = [];
    private double _containerWidth;
    private readonly string _filePath;
    private readonly PDFiumService _pdfiumService;
    private int _pageCount;

    // Background render channel: bounded queue, drops oldest when full.
    private readonly Channel<int> _renderChannel = Channel.CreateBounded<int>(
        new BoundedChannelOptions(7) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _renderTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherQueue _dispatcher;
    private bool _disposed;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public ImagesVirtualizingCollection(string path)
    {
        _filePath = path;
        _pdfiumService = new PDFiumService();
        _pageCount = _pdfiumService.GetPageCount(_filePath);
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("ImagesVirtualizingCollection 必须在 UI 线程创建");

        _renderTask = Task.Run(RenderLoopAsync);
    }

    // ════════════════════════════════════════════════════════════
    //  Placeholder initialization (batch parallel render)
    // ════════════════════════════════════════════════════════════

    internal async Task InitializePlaceholdersAsync()
    {
        var swTotal = Stopwatch.StartNew();
        int[] allPages = Enumerable.Range(0, _pageCount).ToArray();

        // Phase 1 — background thread: parallel PDFium render to raw bytes.
        var swPhase1 = Stopwatch.StartNew();
        var rawBuffers = await Task.Run(() =>
            _pdfiumService.RenderPagesToRawBuffers(_filePath, allPages, scale: 1f));
        swPhase1.Stop();
        Debug.WriteLine($"[PDFium] Phase 1 (render {_pageCount} pages) took {swPhase1.Elapsed.TotalMilliseconds:F1} ms");

        // Phase 2 — UI thread: create WriteableBitmap from raw bytes.
        var swPhase2 = Stopwatch.StartNew();
        for (int i = 0; i < _pageCount; i++)
        {
            var (w, h, pixels) = rawBuffers[i];
            var bitmap = PDFiumService.CreateWriteableBitmapFromRawBuffer(w, h, pixels);
            // _pageSizes stores rendered dimensions at scale=1, which equals
            // PDF points (72 DPI → 1 pixel = 1 point).
            _pageSizes[i] = (w, h);
            _placeHolderCache[i] = new PageImageItem { Image = bitmap, PageIndex = i };
        }
        swPhase2.Stop();
        Debug.WriteLine($"[PDFium] Phase 2 (create bitmaps) took {swPhase2.Elapsed.TotalMilliseconds:F1} ms");

        // Phase 2b — load page-bounding-box origins for correct coordinate mapping.
        var bboxes = _pdfiumService.GetAllPageBBoxes(_filePath);
        for (int i = 0; i < bboxes.Length; i++)
        {
            _pageOrigins[i] = (bboxes[i].Left, bboxes[i].Bottom);
        }

        swTotal.Stop();
        Debug.WriteLine($"[PDFium] InitializePlaceholdersAsync total: {swTotal.Elapsed.TotalMilliseconds:F1} ms");

//#if DEBUG
//        await EvaluatePlaceholderCacheMemoryAsync();
//#endif
    }

    private async Task EvaluatePlaceholderCacheMemoryAsync()
    {
        long totalPixels = 0;
        long totalBitmapBytes = 0;
        long totalPngBytes = 0;
        int entriesWithImage = 0;
        int entriesWithoutImage = 0;
        long managedOverhead = 0;

        foreach (var kvp in _placeHolderCache)
        {
            var item = kvp.Value;
            if (item?.Image is { } bitmap)
            {
                entriesWithImage++;
                int w = bitmap.PixelWidth;
                int h = bitmap.PixelHeight;
                long pixels = (long)w * h;
                totalPixels += pixels;
                // WriteableBitmap is BGRA32 = 4 bytes per pixel
                long bitmapBytes = pixels * 4;
                totalBitmapBytes += bitmapBytes;
                // Managed object overhead estimate per PageImageItem + WriteableBitmap
                managedOverhead += 128; // ~64 bytes per object

                // Encode as PNG to measure compressed size
                try
                {
                    byte[] pixelData;
                    using (var pixelStream = bitmap.PixelBuffer.AsStream())
                    {
                        pixelData = new byte[pixelStream.Length];
                        pixelStream.Read(pixelData, 0, pixelData.Length);
                    }

                    var stream = new InMemoryRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        (uint)w, (uint)h,
                        96, 96,
                        pixelData);
                    await encoder.FlushAsync();
                    totalPngBytes += (long)stream.Size;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PDFium] PNG encode failed for page {kvp.Key}: {ex.Message}");
                }
            }
            else
            {
                entriesWithoutImage++;
            }
        }

        // Dictionary overhead: buckets + entries (approximate)
        long dictOverhead = _placeHolderCache.Count * 48;

        long estimatedTotal = totalBitmapBytes + managedOverhead + dictOverhead;
        double totalMB = estimatedTotal / (1024.0 * 1024.0);

        double rawMB = totalBitmapBytes / (1024.0 * 1024.0);
        double pngMB = totalPngBytes / (1024.0 * 1024.0);
        double ratio = totalBitmapBytes > 0
            ? (double)totalPngBytes / totalBitmapBytes * 100
            : 0;

        Debug.WriteLine(
            $"[PDFium] _placeHolderCache memory — " +
            $"entries: {_placeHolderCache.Count} " +
            $"(with image: {entriesWithImage}, w/o image: {entriesWithoutImage}), " +
            $"total pixels: {totalPixels:N0}, " +
            $"raw BGRA32: {rawMB:F1} MB, " +
            $"PNG compressed: {pngMB:F1} MB ({ratio:F1}%), " +
            $"estimated total: {totalMB:F1} MB");

        // Per-page breakdown for large caches (>10 pages)
        if (_placeHolderCache.Count > 10)
        {
            long avgBitmapKB = entriesWithImage > 0
                ? totalBitmapBytes / entriesWithImage / 1024
                : 0;
            long avgPngKB = entriesWithImage > 0
                ? totalPngBytes / entriesWithImage / 1024
                : 0;
            Debug.WriteLine(
                $"[PDFium] _placeHolderCache per-page avg: raw {avgBitmapKB} KB, " +
                $"PNG {avgPngKB} KB " +
                $"({totalBitmapBytes / 1024 / 1024} MB → {totalPngBytes / 1024 / 1024} MB / {entriesWithImage} bitmaps)");
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Layout: calculate display widths from page aspect ratios
    // ════════════════════════════════════════════════════════════

    public void CalculateDisplaySizes(double availableWidth)
    {
        if (_pageSizes.Count == 0) return;

        _containerWidth = availableWidth;

        double maxWidth = 0;
        foreach (var (w, _) in _pageSizes.Values)
        {
            if (w > maxWidth) maxWidth = w;
        }

        lock (_cacheLock)
        {
            foreach (var cacheEntry in _placeHolderCache)
            {
                double scale = _pageSizes[cacheEntry.Key].Width / maxWidth;
                var item = cacheEntry.Value;
                item.Width = availableWidth * scale;
                item.ContainerWidth = _containerWidth;

                // Keep Height in sync with the real displayed height so that
                // HitTestPageImage's manual layout scan stays aligned with the
                // rendered Image.
                var bitmap = item.Image;
                if (bitmap != null)
                {
                    item.Height = item.Width * (bitmap.PixelHeight / (double)bitmap.PixelWidth);
                }
                else
                {
                    var (_, h) = _pageSizes[cacheEntry.Key];
                    item.Height = item.Width * (h / _pageSizes[cacheEntry.Key].Width);
                }
            }
            for (var node = _highResCache.First; node != null; node = node.Next)
            {
                double scale = _pageSizes[node.Value.Key].Width / maxWidth;
                var item = node.Value.Value;
                item.Width = availableWidth * scale;
                item.ContainerWidth = _containerWidth;

                var bitmap = item.Image;
                if (bitmap != null)
                {
                    item.Height = item.Width * (bitmap.PixelHeight / (double)bitmap.PixelWidth);
                }
                else
                {
                    var (_, h) = _pageSizes[node.Value.Key];
                    item.Height = item.Width * (h / _pageSizes[node.Value.Key].Width);
                }
            }
        }

        // No longer firing Reset — PageImageItem.PropertyChanged handles UI updates.
    }

    // ════════════════════════════════════════════════════════════
    //  Dynamic DPI switching
    // ════════════════════════════════════════════════════════════

    public void SetRenderDpi(int newDpi)
    {
        // Reserved for future use: re-render cached pages at new DPI.
    }

    private void ReRenderCachedPages()
    {
        List<int> cachedIndices;
        lock (_cacheLock)
        {
            cachedIndices = new List<int>(_highResCache.Count);
            for (var node = _highResCache.First; node != null; node = node.Next)
            {
                cachedIndices.Add(node.Value.Key);
            }
        }

        foreach (int index in cachedIndices)
        {
            EnqueueRender(index);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Background render loop
    // ════════════════════════════════════════════════════════════

    private async Task RenderLoopAsync()
    {
        try
        {
            while (await _renderChannel.Reader.WaitToReadAsync(_cts.Token))
            {
                if (!_renderChannel.Reader.TryRead(out var index))
                    continue;

                // Check high-res cache — if already present, move to LRU tail & notify UI.
                PageImageItem? cachedItem = null;
                lock (_cacheLock)
                {
                    var node = _highResCache.First;
                    while (node != null)
                    {
                        if (node.Value.Key == index)
                        {
                            var value = node.Value;
                            _highResCache.Remove(node);
                            _highResCache.AddLast(value);
                            cachedItem = value.Value;
                            break;
                        }
                        node = node.Next;
                    }
                }

                if (cachedItem != null)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        _placeHolderCache.TryGetValue(index, out var oldValue);
                        Debug.WriteLine("Loop rerender in cache");
                        OnCollectionChanged(
                            new NotifyCollectionChangedEventArgs(
                                NotifyCollectionChangedAction.Replace,
                                cachedItem,
                                oldValue,
                                index
                            )
                        );
                    });
                    continue;
                }

                // Phase 1 — background thread: PDFium render to raw bytes.
                // scale = dpi / 72 (e.g. 400/72 ≈ 5.56, 600/72 ≈ 8.33).
                int renderDpi = _currentRenderDpi;
                float scale = renderDpi / 72f;
                var (width, height, pixels) = _pdfiumService.RenderPageToRawBuffer(
                    _filePath, index, scale);

                // Phase 2 — UI thread: create WriteableBitmap & finalize.
                int capturedDpi = renderDpi;
                
                _dispatcher.TryEnqueue(() => FinalizeRender(index, width, height, pixels, capturedDpi));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Called on the UI thread: creates a <see cref="WriteableBitmap"/> from the raw
    /// buffer, manages the high-res cache, and fires <see cref="CollectionChanged"/>
    /// so the ItemsRepeater picks up the new bitmap.
    /// </summary>
    private void FinalizeRender(int index, int width, int height, byte[] pixels, int renderDpi)
    {
        if (renderDpi != _currentRenderDpi)
            return;

        // WriteableBitmap must be created on the UI thread — and we are on it.
        var bitmap = PDFiumService.CreateWriteableBitmapFromRawBuffer(width, height, pixels);

        double placeholderWidth = _placeHolderCache.TryGetValue(index, out var ph) ? ph.Width : double.NaN;
        double itemHeight = double.NaN;
        if (!double.IsNaN(placeholderWidth) && placeholderWidth > 0 && width > 0)
        {
            itemHeight = placeholderWidth * (height / (double)width);
        }

        var item = new PageImageItem
        {
            Image = bitmap,
            PageIndex = index,
            Width = placeholderWidth,
            Height = itemHeight,
            ContainerWidth = _containerWidth
        };

        lock (_cacheLock)
        {
            // Add to tail (most recent).
            _highResCache.AddLast(new KeyValuePair<int, PageImageItem>(index, item));
            // Evict oldest when over capacity.
            while (_highResCache.Count > MaxHighResCacheSize)
            {
                _highResCache.RemoveFirst();
            }
        }

        _placeHolderCache.TryGetValue(index, out var oldValue);
        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Replace,
                item,
                oldValue,
                index
            )
        );
    }

    private void EnqueueRender(int index)
    {
        _renderChannel.Writer.TryWrite(index);
    }

    // ════════════════════════════════════════════════════════════
    //  Public helpers for text-layer coordinate mapping
    // ════════════════════════════════════════════════════════════

    /// <summary>PDF page dimensions in points (1/72 inch).</summary>
    public (double WidthPt, double HeightPt) GetPageSizeInPoints(int pageIndex)
    {
        if (_pageSizes.TryGetValue(pageIndex, out var size))
            return size; // scale=1 → pixels = points
        return (612, 792); // fallback: US Letter
    }

    /// <summary>CropBox/MediaBox origin offset (left, bottom) in PDF points.</summary>
    public (double Left, double Bottom) GetPageOrigin(int pageIndex)
    {
        if (_pageOrigins.TryGetValue(pageIndex, out var origin))
            return origin;
        return (0, 0);
    }

    /// <summary>Available width for page display (ScrollView width minus margins).</summary>
    public double ContainerWidth => _containerWidth;

    /// <summary>
    /// Returns the placeholder item for <paramref name="index"/> without any
    /// side effects — no render enqueue, no LRU manipulation.  Safe to call
    /// from hot paths such as hit‑testing.
    /// </summary>
    public PageImageItem? GetPlaceholder(int index)
    {
        _placeHolderCache.TryGetValue(index, out var item);
        return item;
    }

    /// <summary>Underlying PDFium service — used by text selection layer.</summary>
    public PDFiumService PdfiumService => _pdfiumService;

    /// <summary>File path of the opened document.</summary>
    public string FilePath => _filePath;

    // ════════════════════════════════════════════════════════════
    //  IList — ItemsRepeater virtualizing access
    // ════════════════════════════════════════════════════════════

    public object? this[int index]
    {
        get
        {
            //Debug.WriteLine("Get index {0}", index);
            lock (_cacheLock)
            {
               for (var node = _highResCache.First; node != null; node = node.Next)
               {
                   if (node.Value.Key == index)
                   {
                       var value = node.Value;
                       _highResCache.Remove(node);
                       _highResCache.AddLast(value);
                       return value.Value;
                   }
               }
            }

            EnqueueRender(index);

            _placeHolderCache.TryGetValue(index, out PageImageItem? placeHolderItem);
            return placeHolderItem;
        }
        set
        {
            // Set by ItemsRepeater internally; handled via Replace notification.
        }
    }

    public int Count => _pageCount;

    protected virtual void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        CollectionChanged?.Invoke(this, e);
    }

    // ════════════════════════════════════════════════════════════
    //  IDisposable
    // ════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _renderChannel.Writer.Complete();
        try { _renderTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        _cts.Dispose();
        _pdfiumService.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  Remaining IList members (unused by ItemsRepeater)
    // ════════════════════════════════════════════════════════════

    public bool IsFixedSize => true;
    public bool IsReadOnly => true;

    public bool Contains(object? value) => false;
    public int IndexOf(object? value) => -1;
    public void Clear() { }
    public void Insert(int index, object? value) { }
    public void Remove(object? value) { }
    public void RemoveAt(int index) { }
    public void CopyTo(Array array, int index) { }

    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public IEnumerator GetEnumerator() =>
        throw new NotImplementedException("ListView 不使用 Enumerator 进行虚拟化");

    public int Add(object? value)
    {
        throw new NotImplementedException();
    }
}
