using System;
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

    // Zoom-DPI thresholds
    private const double ZoomThreshold = 2.0;
    private const int DefaultRenderDpi = 500;
    private const int HighRenderDpi = 600;
    private bool _isHighDpiMode = false;
    private DispatcherTimer? _zoomDebounceTimer;

    public int RenderedPageCount
    {
        get => VirtualizingImages.Count;
    }

    public PdfViewerPage(string filename)
    {
        FileName = filename;
        VirtualizingImages = new ImagesVirtualizingCollection(FileName);
        InitializeComponent();
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

    private void PdfScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double availableWidth = e.NewSize.Width - 64; // ItemsRepeater margin (32 * 2)
        if (availableWidth > 0)
        {
            VirtualizingImages.CalculateDisplaySizes(availableWidth);
        }
    }

    private void PdfScrollViewer_ViewChanged(ScrollView sender, object args)
    {
        if (_zoomDebounceTimer == null)
        {
            _zoomDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _zoomDebounceTimer.Tick += OnZoomDebounceTick;
        }
        _zoomDebounceTimer.Stop();
        _zoomDebounceTimer.Start();
    }

    private void OnZoomDebounceTick(object? sender, object e)
    {
        _zoomDebounceTimer?.Stop();

        bool shouldBeHighDpi = PdfScrollViewer.ZoomFactor >= ZoomThreshold;
        //Debug.WriteLine($"zoom: {PdfScrollViewer.ZoomFactor}");
        if (shouldBeHighDpi != _isHighDpiMode)
        {
            _isHighDpiMode = shouldBeHighDpi;
            int newDpi = shouldBeHighDpi ? HighRenderDpi : DefaultRenderDpi;
            VirtualizingImages.SetRenderDpi(newDpi);
        }
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

    // Cursor-tracking state (throttled to avoid per-pixel text-page queries)
    private Point _lastCursorCheckPoint;
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

        var hit = HitTestPageImage(e);
        if (hit.Image == null || hit.Item == null) return;

        var (pwPt, phPt) = VirtualizingImages.GetPageSizeInPoints(hit.Item.PageIndex);
        var pdfPoint = ImagePointToPdf(hit.Image, hit.Item, hit.LocalPoint, pwPt, phPt);
        if (pdfPoint == null) return;

        var textPage = LoadTextPageForSelection(hit.Item.PageIndex);
        if (textPage == null) return;

        int charIndex = PDFiumService.TextGetCharIndexAtPos(
            textPage, pdfPoint.Value.X, pdfPoint.Value.Y, 10, 10);
        if (charIndex < 0) return;

        _isSelecting = true;
        _selectionStartPage = hit.Item.PageIndex;
        _selectionEndPage = hit.Item.PageIndex;
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

        var hit = HitTestPageImage(e);
        if (hit.Image == null || hit.Item == null) return;

        int curPage = hit.Item.PageIndex;
        var curTextPage = LoadTextPageForSelection(curPage);
        if (curTextPage == null) return;

        var (pwPt, phPt) = VirtualizingImages.GetPageSizeInPoints(curPage);
        var pdfPoint = ImagePointToPdf(hit.Image, hit.Item, hit.LocalPoint, pwPt, phPt);
        if (pdfPoint == null) return;

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
    }

    // ════════════════════════════════════════════════════════════════
    //  Hit-testing & coordinate mapping
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Finds the <see cref="Image"/> element under the pointer and returns
    /// its data context plus the local pointer position.
    /// </summary>
    private (Image? Image, PageImageItem? Item, Point LocalPoint) HitTestPageImage(
        PointerRoutedEventArgs e)
    {
        // Compute which page the pointer is over by walking the known
        // page layout (avoids ItemsRepeater hit-test quirks).
        var repeaterPoint = e.GetCurrentPoint(PagesRepeater).Position;

        double yCursor = 0;
        const double spacing = 16;

        for (int i = 0; i < VirtualizingImages.Count; i++)
        {
            if (VirtualizingImages[i] is not PageImageItem item) continue;
            if (item.Image is not { } bitmap) continue;

            var (pageW, pageH) = VirtualizingImages.GetPageSizeInPoints(i);
            double dispW = item.Width;
            if (double.IsNaN(dispW) || dispW <= 0) continue;
            double dispH = dispW * (pageH / pageW);

            double pageTop = yCursor;
            double pageBottom = yCursor + dispH;

            if (repeaterPoint.Y >= pageTop && repeaterPoint.Y < pageBottom)
            {
                // Found the page — get the visual Image element.
                var element = PagesRepeater.GetOrCreateElement(i);
                var image = FindImageInTree(element);
                if (image != null)
                {
                    var localPoint = e.GetCurrentPoint(image).Position;
                    return (image, item, localPoint);
                }
            }

            yCursor += dispH + spacing;
        }

        return (null, null, default);
    }

    /// <summary>Depth-first search for the first <see cref="Image"/> child.</summary>
    private static Image? FindImageInTree(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Image img)
                return img;
            var found = FindImageInTree(child);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Converts a point inside the <see cref="Image"/> element (in DIPs)
    /// to PDF user-space coordinates (in points, origin bottom-left).
    /// Returns null when the image has no bitmap yet.
    /// </summary>
    private static Point? ImagePointToPdf(Image image, PageImageItem item,
        Point imageLocalPoint, double pageWidthPt, double pageHeightPt)
    {
        if (item.Image is not { } bitmap) return null;

        double imgW = image.ActualWidth;
        double imgH = image.ActualHeight;
        double bmpW = bitmap.PixelWidth;
        double bmpH = bitmap.PixelHeight;

        if (imgW <= 0 || imgH <= 0 || bmpW <= 0 || bmpH <= 0) return null;

        // Uniform stretch: bitmap is centered inside the Image control.
        double scale = Math.Min(imgW / bmpW, imgH / bmpH);
        double displayedW = bmpW * scale;
        double displayedH = bmpH * scale;
        double offsetX = (imgW - displayedW) / 2;
        double offsetY = (imgH - displayedH) / 2;

        double bitmapX = Math.Clamp(imageLocalPoint.X - offsetX, 0, displayedW);
        double bitmapY = Math.Clamp(imageLocalPoint.Y - offsetY, 0, displayedH);

        // Fraction within the displayed bitmap → PDF points.
        double pdfX = (bitmapX / displayedW) * pageWidthPt;
        double pdfY = pageHeightPt - (bitmapY / displayedH) * pageHeightPt; // flip Y

        return new Point(pdfX, pdfY);
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
        var viewportPoint = e.GetCurrentPoint(PdfScrollViewer).Position;

        // Throttle — skip tiny movements on the same page.
        double dx = Math.Abs(viewportPoint.X - _lastCursorCheckPoint.X);
        double dy = Math.Abs(viewportPoint.Y - _lastCursorCheckPoint.Y);
        if (dx < 5 && dy < 5)
            return;

        var hit = HitTestPageImage(e);
        if (hit.Image == null || hit.Item == null)
        {
            ProtectedCursor = null; // reset to default
            _lastCursorCheckPage = -1;
            return;
        }

        // If we already know this page has no text, skip re-querying.
        if (hit.Item.PageIndex == _lastCursorCheckPage && dx < 5 && dy < 5)
            return;

        _lastCursorCheckPoint = viewportPoint;
        _lastCursorCheckPage = hit.Item.PageIndex;

        var textPage = LoadTextPageForSelection(hit.Item.PageIndex);
        if (textPage == null)
        {
            ProtectedCursor = null;
            return;
        }

        var (pwPt, phPt) = VirtualizingImages.GetPageSizeInPoints(hit.Item.PageIndex);
        var pdfPoint = ImagePointToPdf(hit.Image, hit.Item, hit.LocalPoint, pwPt, phPt);
        if (pdfPoint == null)
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

        // ── 3. Get Image layout info ─────────────────────────────────
        var element = PagesRepeater.GetOrCreateElement(pageIndex);
        var image = FindImageInTree(element);
        if (image == null) return;

        var transform = image.TransformToVisual(HighlightOverlay);
        var imageOrigin = transform.TransformPoint(new Point(0, 0));

        if (image.DataContext is not PageImageItem item) return;
        if (item.Image is not { } bitmap) return;

        double imgW = image.ActualWidth;
        double imgH = image.ActualHeight;
        double bmpW = bitmap.PixelWidth;
        double bmpH = bitmap.PixelHeight;
        if (imgW <= 0 || imgH <= 0 || bmpW <= 0 || bmpH <= 0) return;

        double bmpScale = Math.Min(imgW / bmpW, imgH / bmpH);
        double displayedW = bmpW * bmpScale;
        double displayedH = bmpH * bmpScale;
        double offsetX = (imgW - displayedW) / 2;
        double offsetY = (imgH - displayedH) / 2;

        var (pageW, pageH) = VirtualizingImages.GetPageSizeInPoints(pageIndex);
        double scaleX = displayedW / pageW;
        double scaleY = displayedH / pageH;

        // ── 4. Draw merged rects ─────────────────────────────────────
        foreach (var (l, t, r, b) in merged)
        {
            double winX = imageOrigin.X + offsetX + l * scaleX;
            double winY = imageOrigin.Y + offsetY + (pageH - t) * scaleY;
            double winW = (r - l) * scaleX;
            double winH = (t - b) * scaleY;
            if (winW <= 0 || winH <= 0) continue;

            var rect = new Rectangle
            {
                Fill = new SolidColorBrush(HighlightColor),
                Width = winW,
                Height = winH,
            };
            Canvas.SetLeft(rect, winX);
            Canvas.SetTop(rect, winY);
            HighlightOverlay.Children.Add(rect);
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

public class PageImageItem
{
    /// <summary>
    /// Rendered page bitmap. <see cref="WriteableBitmap"/> is used directly
    /// as <see cref="Image.Source"/> — no PNG round-trip needed.
    /// </summary>
    public WriteableBitmap? Image { get; set; }
    public double Width { get; set; } = double.NaN;
    public double Height { get; set; } = double.NaN;
    public double ContainerWidth { get; set; } = double.NaN;

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
    private readonly Dictionary<int, double> _displayWidths = [];
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

        foreach (var pageEntry in _pageSizes)
        {
            double scale = pageEntry.Value.Width / maxWidth;
            _displayWidths[pageEntry.Key] = availableWidth * scale;
        }

        lock (_cacheLock)
        {
            foreach (var cacheEntry in _placeHolderCache)
            {
                cacheEntry.Value.ContainerWidth = _containerWidth;
                if (_displayWidths.TryGetValue(cacheEntry.Key, out var w))
                {
                    cacheEntry.Value.Width = w;
                }
            }
            for (var node = _highResCache.First; node != null; node = node.Next)
            {
                node.Value.Value.ContainerWidth = _containerWidth;
                if (_displayWidths.TryGetValue(node.Value.Key, out var w))
                {
                    node.Value.Value.Width = w;
                }
            }
        }

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
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

                // Check high-res cache — if already present, move to LRU tail & skip.
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
                            break;
                        }
                        node = node.Next;
                    }
                    if (node != null) continue;
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

        var item = new PageImageItem
        {
            Image = bitmap,
            PageIndex = index,
            Width = _displayWidths.GetValueOrDefault(index, double.NaN),
            ContainerWidth = _displayWidths.Count > 0 ? _containerWidth : double.NaN
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
