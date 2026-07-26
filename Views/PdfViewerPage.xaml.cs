using System;
using System.ComponentModel;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using pdf_studio.Models;
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

        // ListViewItem containers may swallow pointer events for their own
        // interaction logic — hook the ScrollView handlers with
        // handledEventsToo so text selection keeps working regardless.
        PdfScrollViewer.AddHandler(PointerPressedEvent,
            new PointerEventHandler(PdfScrollViewer_PointerPressed), true);
        PdfScrollViewer.AddHandler(PointerMovedEvent,
            new PointerEventHandler(PdfScrollViewer_PointerMoved), true);
        PdfScrollViewer.AddHandler(PointerReleasedEvent,
            new PointerEventHandler(PdfScrollViewer_PointerReleased), true);
        PdfScrollViewer.AddHandler(PointerExitedEvent,
            new PointerEventHandler(PdfScrollViewer_PointerExited), true);

        BuildColorPalette();
        VirtualizingImages.PageBitmapChanged += OnPageBitmapChanged;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        // Render all placeholder images (parallel, scale=1)
        await VirtualizingImages.InitializePlaceholdersAsync();

        // Switch visibility
        LoadingOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        PdfScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

        UpdatePageIndicator();
        await Task.WhenAll(LoadHighlightsAsync(), LoadTocAsync());
    }

    private double _lastAvailableWidth = -1;

    private void PdfScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double availableWidth = e.NewSize.Width - 64; // page list margin (32 * 2)
        if (availableWidth > 0 && Math.Abs(availableWidth - _lastAvailableWidth) > 1.0)
        {
            _lastAvailableWidth = availableWidth;
            VirtualizingImages.CalculateDisplaySizes(availableWidth);
            UpdatePageIndicator();
            ScheduleHighResRender();
            RedrawAllOverlays();
            // The list applies the new widths via bindings asynchronously —
            // schedule a second pass so highlights are mapped with settled sizes.
            ScheduleOverlayRedraw();
        }
    }

    private void PdfScrollViewer_ViewChanged(ScrollView sender, object args)
    {
        UpdatePageIndicator();

        // A pending TOC-navigation correction retries here too — the
        // completed scroll is what makes the list realize the target page.
        //if (_pendingCorrectionPage >= 0)
        //    ApplyScrollCorrection();

        // The ListView realizes every container up front (its inner scrolling
        // is disabled), so nothing enqueues high-res renders on its own —
        // trigger them from the viewport instead.
        ScheduleHighResRender();

        // Debounce a redraw of the overlays so scrolling does not leave
        // stale or missing highlights on newly realized pages.
        ScheduleOverlayRedraw();
    }

    // ════════════════════════════════════════════════════════════════
    //  Viewport-driven high-res rendering
    // ════════════════════════════════════════════════════════════════

    private DispatcherTimer? _highResRenderTimer;

    /// <summary>
    /// Debounced high-res render request (120 ms).  Used by scroll/zoom and
    /// size changes to keep the pages around the viewport sharp.
    /// </summary>
    private void ScheduleHighResRender()
    {
        if (_highResRenderTimer == null)
        {
            _highResRenderTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _highResRenderTimer.Tick += (_, _) =>
            {
                _highResRenderTimer?.Stop();
                EnsureVisiblePagesHighRes();
            };
        }

        _highResRenderTimer.Stop();
        _highResRenderTimer.Start();
    }

    /// <summary>
    /// Enqueues high-res renders for the pages intersecting the viewport
    /// (plus a one-page margin on each side).
    /// </summary>
    private void EnsureVisiblePagesHighRes()
    {
        int total = VirtualizingImages.Count;
        if (total == 0) return;

        // Layout not done yet (page heights still NaN/zero).
        if (!((VirtualizingImages.GetPageItem(0)?.Height ?? 0) > 0)) return;

        double zoom = PdfScrollViewer.ZoomFactor;
        double top = PdfScrollViewer.VerticalOffset / zoom;
        double bottom = top + PdfScrollViewer.ViewportHeight / zoom;

        int first = Math.Max(0, FindPageIndexAtOffset(top) - 1);
        int last = Math.Min(total - 1, FindPageIndexAtOffset(bottom) + 1);

        for (int i = first; i <= last; i++)
            VirtualizingImages.EnsureHighResRender(i);
    }

    /// <summary>
    /// Binary-searches the cached page-top-offset table (sorted, unzoomed
    /// DIPs) for the page containing <paramref name="y"/>.
    /// </summary>
    private int FindPageIndexAtOffset(double y)
    {
        int lo = 0, hi = VirtualizingImages.Count - 1, page = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (VirtualizingImages.GetPageTopOffset(mid) <= y)
            {
                page = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return page;
    }

    /// <summary>
    /// Debounced overlay redraw (50 ms).  Used by scroll/zoom changes and
    /// by any layout change whose visual-tree updates settle asynchronously.
    /// </summary>
    private void ScheduleOverlayRedraw()
    {
        if (_highlightRedrawTimer == null)
        {
            _highlightRedrawTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _highlightRedrawTimer.Tick += (_, _) =>
            {
                _highlightRedrawTimer?.Stop();
                RedrawAllOverlays();
            };
        }

        _highlightRedrawTimer.Stop();
        _highlightRedrawTimer.Start();
    }

    /// <summary>
    /// A page's bitmap was swapped in place (high-res upgrade, or downgrade
    /// after LRU eviction).  The displayed DIP size never changes, but overlay
    /// rects are mapped through the bitmap's pixel size — redraw when the page
    /// is inside the current selection or carries persistent highlights.
    /// </summary>
    private void OnPageBitmapChanged(object? sender, int pageIndex)
    {
        bool needsRedraw = false;
        if (_selectionStartPage >= 0 && _selectionEndPage >= 0)
        {
            int from = Math.Min(_selectionStartPage, _selectionEndPage);
            int to = Math.Max(_selectionStartPage, _selectionEndPage);
            needsRedraw = pageIndex >= from && pageIndex <= to;
        }

        if (!needsRedraw)
            needsRedraw = _highlights.Any(h => h.PageIndex == pageIndex);

        if (needsRedraw)
            RedrawAllOverlays();
    }

    // ════════════════════════════════════════════════════════════════
    //  Toolbar: TOC, highlight pen, page indicator
    // ════════════════════════════════════════════════════════════════

    private void TocSplitView_PaneToggled(SplitView sender, object args)
    {
        // Pane animation changed the content width; layout/bindings may still
        // be settling — redraw now and once more shortly after.
        RedrawAllOverlays();
        ScheduleOverlayRedraw();
    }

    private void HighlightPenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _isHighlightMode = HighlightPenToggle.IsChecked == true;
        ColorPalettePanel.Visibility = _isHighlightMode
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        ClearSelection();
    }

    /// <summary>Highlighter pen colours offered in the toolbar palette.</summary>
    private static readonly Windows.UI.Color[] PenColors =
    [
        Windows.UI.Color.FromArgb(255, 255, 235, 59),  // yellow
        Windows.UI.Color.FromArgb(255, 105, 240, 174), // green
        Windows.UI.Color.FromArgb(255, 64, 196, 255),  // blue
        Windows.UI.Color.FromArgb(255, 255, 64, 129),  // pink
        Windows.UI.Color.FromArgb(255, 255, 171, 64),  // orange
    ];

    private Windows.UI.Color _penColor = PenColors[0];

    private void BuildColorPalette()
    {
        foreach (var color in PenColors)
        {
            var swatch = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush(color),
            };
            var button = new Button
            {
                Content = swatch,
                Padding = new Thickness(4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(
                    color == _penColor
                        ? Windows.UI.Color.FromArgb(255, 0, 120, 215)
                        : Microsoft.UI.Colors.Transparent),
                Tag = color,
            };
            ToolTipService.SetToolTip(button, "高亮颜色");
            button.Click += OnPenColorClicked;
            ColorPalettePanel.Children.Add(button);
        }
    }

    private void OnPenColorClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Windows.UI.Color color })
        {
            _penColor = color;
            foreach (var child in ColorPalettePanel.Children.OfType<Button>())
            {
                bool selected = child.Tag is Windows.UI.Color c && c == color;
                child.BorderBrush = new SolidColorBrush(
                    selected
                        ? Windows.UI.Color.FromArgb(255, 0, 120, 215)
                        : Microsoft.UI.Colors.Transparent);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Page indicator & navigation
    // ════════════════════════════════════════════════════════════════

    private void UpdatePageIndicator()
    {
        int total = VirtualizingImages.Count;
        if (total == 0)
        {
            PageIndicator.Text = "– / –";
            return;
        }

        // Layout not done yet (page heights all zero) — report page 1.
        if ((VirtualizingImages.GetPageItem(0)?.Height ?? 0) <= 0)
        {
            PageIndicator.Text = $"1 / {total}";
            return;
        }

        double zoom = PdfScrollViewer.ZoomFactor;
        double top = PdfScrollViewer.VerticalOffset / zoom;

        // Cached offsets are sorted — binary search the page containing the
        // viewport top, matching TOC navigation which aligns the page to the top.
        int page = FindPageIndexAtOffset(top);

        PageIndicator.Text = $"{page + 1} / {total}";
    }

    /// <summary>Scrolls so that the top of <paramref name="pageIndex"/> is visible.</summary>
    private void ScrollToPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= VirtualizingImages.Count) return;

        double y = VirtualizingImages.GetPageTopOffset(pageIndex);

        double zoom = PdfScrollViewer.ZoomFactor;
        //Debug.WriteLine($"[Scroll] Page {pageIndex + 1} all offset: {VirtualizingImages.GetPageTopOffset(VirtualizingImages.Count)}, zoom: {zoom}");

        //Debug.Print($"{PdfScrollViewer.ScrollableHeight}");
        // Instant jump (standard PDF-reader behaviour)…
        PdfScrollViewer.ScrollTo(
            PdfScrollViewer.HorizontalOffset,
            y * zoom,
            new ScrollingScrollOptions(
                ScrollingAnimationMode.Disabled,
                ScrollingSnapPointsMode.Ignore));
    }

    /// <summary>
    /// Fine-tunes the scroll offset using the realized page element's
    /// actual visual position.  Retries a few times while the target page
    /// is still being materialized by the ListView.
    /// </summary>

    // ════════════════════════════════════════════════════════════════
    //  Table of contents
    // ════════════════════════════════════════════════════════════════

    private async Task LoadTocAsync()
    {
        try
        {
            var items = await Task.Run(() =>
                VirtualizingImages.PdfiumService.GetBookmarks(FileName));

            if (items.Count == 0)
            {
                TocEmptyLabel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                return;
            }

            foreach (var item in items)
                TocTree.RootNodes.Add(BuildTocNode(item));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TOC] Failed to load bookmarks: {ex.Message}");
            TocEmptyLabel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }
    }

    private static TreeViewNode BuildTocNode(TocItem item)
    {
        var node = new TreeViewNode { Content = item };
        foreach (var child in item.Children)
            node.Children.Add(BuildTocNode(child));
        return node;
    }

    private void TocTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: TocItem item } && item.PageIndex >= 0)
            ScrollToPage(item.PageIndex);
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
    //  Persistent highlights (annotation layer)
    // ════════════════════════════════════════════════════════════════

    /// <summary>All highlights currently shown, in no particular order.</summary>
    private readonly List<PdfHighlight> _highlights = [];

    /// <summary>Undo stack: (true = added, false = removed).</summary>
    private readonly Stack<(bool WasAdded, PdfHighlight Highlight)> _undoStack = new();

    /// <summary>Transient in-drag preview rects (overlay coordinates) shown in pen colour.</summary>
    private readonly List<Rect> _pendingPreviewRects = [];

    private bool _isHighlightMode;

    /// <summary>Raised when the unsaved-changes state flips (for the tab header asterisk).</summary>
    public event EventHandler<bool>? DirtyStateChanged;

    private bool _isDirty;
    private bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            DirtyStateChanged?.Invoke(this, value);
        }
    }

    private ContainerVisual? _highlightContainerVisual;
    private readonly Dictionary<Windows.UI.Color, CompositionEffectBrush> _multiplyBrushes = [];

    /// <summary>Redraws both the transient selection overlay and the persistent highlights.</summary>
    private void RedrawAllOverlays()
    {
        DrawSelectionHighlights();
        RebuildPersistentHighlightVisuals();
    }

    private void RebuildPersistentHighlightVisuals()
    {
        EnsureHighlightVisualHost();
        if (_highlightContainerVisual == null) return;

        _highlightContainerVisual.Children.RemoveAll();

        // Drag preview (highlight mode only) in the current pen colour.
        if (_isHighlightMode)
        {
            foreach (var rect in _pendingPreviewRects)
                _highlightContainerVisual.Children.InsertAtTop(
                    CreateMultiplyVisual(rect, _penColor));
        }

        foreach (var highlight in _highlights)
        {
            var image = GetPageImageElement(highlight.PageIndex);
            if (image == null) continue;

            foreach (var r in highlight.Rects)
            {
                var overlayRect = MapPdfRectToOverlayRect((r.L, r.T, r.R, r.B), image, highlight.PageIndex);
                if (overlayRect.IsEmpty || overlayRect.Width <= 0 || overlayRect.Height <= 0)
                    continue;

                _highlightContainerVisual.Children.InsertAtTop(
                    CreateMultiplyVisual(overlayRect, highlight.Color));
            }
        }
    }

    private void EnsureHighlightVisualHost()
    {
        if (_highlightContainerVisual != null) return;

        var compositor = ElementCompositionPreview
            .GetElementVisual(PersistentHighlightOverlay).Compositor;
        _highlightContainerVisual = compositor.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(
            PersistentHighlightOverlay, _highlightContainerVisual);
    }

    /// <summary>
    /// Creates a SpriteVisual that multiply-blends <paramref name="color"/>
    /// with whatever is behind it (CompositionBackdropBrush), so black text
    /// stays black — true highlighter behaviour.
    /// </summary>
    private SpriteVisual CreateMultiplyVisual(Rect rect, Windows.UI.Color color)
    {
        var compositor = _highlightContainerVisual!.Compositor;

        if (!_multiplyBrushes.TryGetValue(color, out var brush))
        {
            var backdrop = new CompositionEffectSourceParameter("backdrop");
            var blend = new BlendEffect
            {
                Background = backdrop,
                Foreground = new ColorSourceEffect { Color = color },
                Mode = BlendEffectMode.Multiply,
            };
            brush = compositor.CreateEffectFactory(blend).CreateBrush();
            brush.SetSourceParameter("backdrop", compositor.CreateBackdropBrush());
            _multiplyBrushes[color] = brush;
        }

        var visual = compositor.CreateSpriteVisual();
        visual.Brush = brush;
        visual.Size = new Vector2((float)rect.Width, (float)rect.Height);
        visual.Offset = new Vector3((float)rect.X, (float)rect.Y, 0);
        return visual;
    }

    // ════════════════════════════════════════════════════════════════
    //  Persistent highlights: load / save / undo
    // ════════════════════════════════════════════════════════════════

    private async Task LoadHighlightsAsync()
    {
        try
        {
            var loaded = await Task.Run(() =>
                VirtualizingImages.PdfiumService.LoadHighlights(FileName));
            _highlights.AddRange(loaded);
            RebuildPersistentHighlightVisuals();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Highlight] Failed to load annotations: {ex.Message}");
        }
    }

    private async void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveHighlightsAsync();
    }

    private void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        UndoLastHighlightOperation();
    }

    private void UndoLastHighlightOperation()
    {
        if (_undoStack.Count == 0) return;

        var (wasAdded, highlight) = _undoStack.Pop();
        if (wasAdded)
            _highlights.Remove(highlight);
        else
            _highlights.Add(highlight);

        IsDirty = true;
        RebuildPersistentHighlightVisuals();
    }

    private async Task SaveHighlightsAsync()
    {
        if (!IsDirty) return;

        string tempPath = FileName + ".highlight-tmp";
        try
        {
            // Release the cached text document handle — overwriting the file
            // fails while PDFium keeps it open for reading.
            VirtualizingImages.PdfiumService.ReleaseAllTextPages();

            var snapshot = _highlights.ToList();
            await Task.Run(() =>
                VirtualizingImages.PdfiumService.SaveHighlightsToTemp(FileName, snapshot, tempPath));

            await Task.Run(() => ReplaceFileWithRetry(tempPath, FileName));

            IsDirty = false;
            ShowInfoTip("高亮已保存");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Highlight] Save failed: {ex}");
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }

            var dialog = new ContentDialog
            {
                Title = "保存失败",
                Content = $"无法将高亮保存到文件：\n{ex.Message}",
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    /// <summary>
    /// Overwrites <paramref name="target"/> with <paramref name="temp"/>,
    /// retrying briefly while the background render loop may hold a
    /// transient read handle on the target.
    /// </summary>
    private static void ReplaceFileWithRetry(string temp, string target)
    {
        const int maxAttempts = 15;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(temp, target, overwrite: true);
                File.Delete(temp);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(200);
            }
        }
    }

    private void ShowInfoTip(string message)
    {
        var tip = new TeachingTip
        {
            Title = message,
            PreferredPlacement = TeachingTipPlacementMode.Center,
            XamlRoot = this.XamlRoot,
        };
        tip.IsOpen = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            tip.IsOpen = false;
        };
        timer.Start();
    }

    // ════════════════════════════════════════════════════════════════
    //  Pointer event handlers (wired in XAML)
    // ════════════════════════════════════════════════════════════════

    private void PdfScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ClearSelection();

        // Highlight mode: clicking an existing highlight pops a delete flyout.
        if (_isHighlightMode && TryShowHighlightDeleteFlyout(e))
        {
            e.Handled = true;
            return;
        }

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

        if (_isHighlightMode)
            CommitHighlightsFromSelection();
        else
            CopySelectionToClipboard();
        // Keep highlights visible until the next PointerPressed clears them.
        _isSelecting = false;
        e.Handled = true;
    }

    /// <summary>
    /// In highlight mode, checks whether the pointer press landed on an
    /// existing persistent highlight; if so shows a delete flyout.
    /// </summary>
    private bool TryShowHighlightDeleteFlyout(PointerRoutedEventArgs e)
    {
        var (item, pdfPoint) = HitTestPageImage(e);
        if (item == null || pdfPoint == null) return false;

        double x = pdfPoint.Value.X, y = pdfPoint.Value.Y;
        var hit = _highlights.FirstOrDefault(h =>
            h.PageIndex == item.PageIndex &&
            h.Rects.Any(r => x >= r.L && x <= r.R && y <= r.T && y >= r.B));
        if (hit == null) return false;

        var flyout = new Flyout();
        var deleteButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14 }, // Delete
                    new TextBlock { Text = "删除高亮", VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        deleteButton.Click += (_, _) =>
        {
            _highlights.Remove(hit);
            _undoStack.Push((false, hit));
            IsDirty = true;
            RebuildPersistentHighlightVisuals();
            flyout.Hide();
        };
        flyout.Content = deleteButton;
        flyout.ShowAt(ContentGrid, new FlyoutShowOptions
        {
            Position = e.GetCurrentPoint(ContentGrid).Position,
        });
        return true;
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
    /// instead of reconstructing the page-stack layout manually, so it stays
    /// aligned with the rendered page after zoom, resize, and high-res replacement.
    /// </remarks>
    private (PageImageItem? Item, Point? PdfPoint) HitTestPageImage(
        PointerRoutedEventArgs e)
    {
        var listPoint = e.GetCurrentPoint(PagesList).Position;

        for (int i = 0; i < VirtualizingImages.Count; i++)
        {
            var image = GetPageImageElement(i);
            if (image == null)
                continue;

            if (image.DataContext is not PageImageItem item)
                continue;

            if (image.ActualWidth <= 0 || image.ActualHeight <= 0)
                continue;

            var bounds = image.TransformToVisual(PagesList)
                .TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight));

            if (bounds.Contains(listPoint))
            {
                var pdfPoint = MapPagePointToPdf(listPoint, image, item.PageIndex);
                if (pdfPoint.HasValue)
                    return (item, pdfPoint.Value);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Returns the realized Image element for a page index, or null if the
    /// ListView has not generated the container for that page yet.
    /// </summary>
    private Image? GetPageImageElement(int pageIndex)
    {
        if (PagesList.ContainerFromIndex(pageIndex) is not DependencyObject container)
            return null;

        return FindFirstDescendant<Image>(container);
    }

    private static T? FindFirstDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            if (FindFirstDescendant<T>(child) is { } found)
                return found;
        }
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
    /// Maps a pointer position expressed in PagesList coordinates to PDF
    /// user-space coordinates for the page represented by the given Image.
    /// </summary>
    private Point? MapPagePointToPdf(Point pointInList, Image image, int pageIndex)
    {
        if (image.ActualWidth <= 0 || image.ActualHeight <= 0)
            return null;

        if (image.Source is not WriteableBitmap bitmap)
            return null;

        var imageBoundsInList = image.TransformToVisual(PagesList)
            .TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight));

        double localX = pointInList.X - imageBoundsInList.X;
        double localY = pointInList.Y - imageBoundsInList.Y;

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

    /// <summary>
    /// Normalizes the current (possibly backwards) selection into a
    /// page/char range.  Returns false when the selection is empty.
    /// </summary>
    private bool TryGetNormalizedSelection(
        out int pageFrom, out int pageTo, out int charFrom, out int charTo)
    {
        pageFrom = pageTo = charFrom = charTo = -1;

        if (_selectionStartPage < 0 || _selectionEndPage < 0) return false;

        pageFrom = Math.Min(_selectionStartPage, _selectionEndPage);
        pageTo = Math.Max(_selectionStartPage, _selectionEndPage);

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

        return charTo > charFrom || pageFrom != pageTo;
    }

    /// <summary>
    /// Computes the (startChar, charCount) slice of the normalized
    /// selection that falls on page <paramref name="p"/>.
    /// </summary>
    private static bool TryGetPageCharRange(
        FpdfTextpageT textPage, int p,
        int pageFrom, int pageTo, int charFrom, int charTo,
        out int startChar, out int charCount)
    {
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

        return charCount > 0;
    }

    private void DrawSelectionHighlights()
    {
        HighlightOverlay.Children.Clear();
        _pendingPreviewRects.Clear();

        if (TryGetNormalizedSelection(out int pageFrom, out int pageTo,
                                      out int charFrom, out int charTo))
        {
            for (int p = pageFrom; p <= pageTo; p++)
            {
                var textPage = LoadTextPageForSelection(p);
                if (textPage == null) continue;

                if (!TryGetPageCharRange(textPage, p, pageFrom, pageTo,
                        charFrom, charTo, out int startChar, out int charCount))
                    continue;

                DrawPageHighlights(p, textPage, startChar, charCount);
            }
        }

        // Drag preview rects feed the persistent overlay in highlight mode.
        RebuildPersistentHighlightVisuals();
    }

    /// <summary>
    /// Draws highlight rectangles for a single page on the overlay Canvas.
    /// Merges adjacent text runs on the same line into continuous blocks.
    /// In highlight mode the rects feed the pen-coloured drag preview
    /// instead of the blue selection overlay.
    /// </summary>
    private void DrawPageHighlights(int pageIndex, FpdfTextpageT textPage,
        int startChar, int charCount)
    {
        var image = GetPageImageElement(pageIndex);
        if (image == null) return;

        var merged = CollectMergedRects(textPage, startChar, charCount);
        if (merged.Count == 0) return;

        foreach (var rect in merged)
        {
            var overlayRect = MapPdfRectToOverlayRect(rect, image, pageIndex);
            if (overlayRect.IsEmpty || overlayRect.Width <= 0 || overlayRect.Height <= 0)
                continue;

            if (_isHighlightMode)
            {
                _pendingPreviewRects.Add(overlayRect);
                continue;
            }

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
    /// Queries PDFium for the raw text rects of a char range and merges
    /// them into line-level blocks (PDF coordinates).
    /// </summary>
    private static List<(double L, double T, double R, double B)> CollectMergedRects(
        FpdfTextpageT textPage, int startChar, int charCount)
    {
        int rectCount = PDFiumService.TextCountRects(textPage, startChar, charCount);
        if (rectCount <= 0) return [];

        var rawRects = new List<(double L, double T, double R, double B)>(rectCount);
        for (int i = 0; i < rectCount; i++)
        {
            if (PDFiumService.TextGetRect(textPage, i,
                    out double l, out double t, out double r, out double b))
                rawRects.Add((l, t, r, b));
        }
        if (rawRects.Count == 0) return [];

        return MergeRectsByLine(rawRects);
    }

    /// <summary>
    /// Converts the current selection into persistent highlights (one per
    /// page) using the current pen colour, and pushes undo entries.
    /// </summary>
    private void CommitHighlightsFromSelection()
    {
        if (!TryGetNormalizedSelection(out int pageFrom, out int pageTo,
                                       out int charFrom, out int charTo))
            return;

        for (int p = pageFrom; p <= pageTo; p++)
        {
            var textPage = LoadTextPageForSelection(p);
            if (textPage == null) continue;

            if (!TryGetPageCharRange(textPage, p, pageFrom, pageTo,
                    charFrom, charTo, out int startChar, out int charCount))
                continue;

            var merged = CollectMergedRects(textPage, startChar, charCount);
            if (merged.Count == 0) continue;

            var highlight = new PdfHighlight(
                p,
                merged.Select(r => new PdfRect(r.L, r.T, r.R, r.B)).ToList(),
                _penColor);
            _highlights.Add(highlight);
            _undoStack.Push((true, highlight));
        }

        IsDirty = true;
        ClearSelection();
        RebuildPersistentHighlightVisuals();
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
        if (_pendingPreviewRects.Count > 0)
        {
            _pendingPreviewRects.Clear();
            RebuildPersistentHighlightVisuals();
        }
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
//  Data item for the page list
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
    // One PageImageItem per page, created once at load and kept forever — a
    // high-res upgrade is an in-place Image property swap on that instance.
    // The ListView therefore never sees a collection change and never
    // rebuilds a container: no blank frame, no full-stack re-layout.
    private readonly Dictionary<int, PageImageItem> _pageItems = [];
    // The original scale=1 bitmaps, kept so an LRU eviction can downgrade a
    // page back to its placeholder and release the big high-res texture.
    private WriteableBitmap?[] _placeholderBitmaps = [];
    // LRU: page index → high-res bitmap (bitmaps only — the page items
    // displaying them live in _pageItems).
    private readonly LinkedList<KeyValuePair<int, WriteableBitmap>> _highResCache = new();
    // Indices currently queued or rendering — deduplicates render requests.
    private readonly HashSet<int> _inflight = [];
    private readonly object _cacheLock = new();
    private const int MaxHighResCacheSize = 12;
    private volatile int _currentRenderDpi = 400;
    private readonly Dictionary<int, (double Width, double Height)> _pageSizes = [];
    private readonly Dictionary<int, (double Left, double Bottom)> _pageOrigins = [];
    private double _containerWidth;
    private readonly string _filePath;
    private readonly PDFiumService _pdfiumService;
    private int _pageCount;

    // Background render channel: bounded queue; when full the newest write is
    // dropped instead — the next viewport pass re-issues the request.
    // (DropOldest would strand the dropped index in _inflight forever.)
    private readonly Channel<int> _renderChannel = Channel.CreateBounded<int>(
        new BoundedChannelOptions(7) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _renderTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherQueue _dispatcher;
    private bool _disposed;

    // Required by INotifyCollectionChanged so the ListView can subscribe, but
    // never raised: items are permanent and upgrades are in-place property
    // updates, so the collection itself never changes after load.
#pragma warning disable CS0067
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
#pragma warning restore CS0067

    /// <summary>
    /// Raised on the UI thread when a page's bitmap is swapped in place —
    /// upgraded to high-res, or downgraded back to the placeholder after an
    /// LRU eviction.  The argument is the page index.
    /// </summary>
    public event EventHandler<int>? PageBitmapChanged;

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
        _placeholderBitmaps = new WriteableBitmap[_pageCount];
        for (int i = 0; i < _pageCount; i++)
        {
            var (w, h, pixels) = rawBuffers[i];
            var bitmap = PDFiumService.CreateWriteableBitmapFromRawBuffer(w, h, pixels);
            // _pageSizes stores rendered dimensions at scale=1, which equals
            // PDF points (72 DPI → 1 pixel = 1 point).
            _pageSizes[i] = (w, h);
            _placeholderBitmaps[i] = bitmap;
            _pageItems[i] = new PageImageItem { Image = bitmap, PageIndex = i };
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

        foreach (var kvp in _pageItems)
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
        long dictOverhead = _pageItems.Count * 48;

        long estimatedTotal = totalBitmapBytes + managedOverhead + dictOverhead;
        double totalMB = estimatedTotal / (1024.0 * 1024.0);

        double rawMB = totalBitmapBytes / (1024.0 * 1024.0);
        double pngMB = totalPngBytes / (1024.0 * 1024.0);
        double ratio = totalBitmapBytes > 0
            ? (double)totalPngBytes / totalBitmapBytes * 100
            : 0;

        Debug.WriteLine(
            $"[PDFium] _pageItems memory — " +
            $"entries: {_pageItems.Count} " +
            $"(with image: {entriesWithImage}, w/o image: {entriesWithoutImage}), " +
            $"total pixels: {totalPixels:N0}, " +
            $"raw BGRA32: {rawMB:F1} MB, " +
            $"PNG compressed: {pngMB:F1} MB ({ratio:F1}%), " +
            $"estimated total: {totalMB:F1} MB");

        // Per-page breakdown for large caches (>10 pages)
        if (_pageItems.Count > 10)
        {
            long avgBitmapKB = entriesWithImage > 0
                ? totalBitmapBytes / entriesWithImage / 1024
                : 0;
            long avgPngKB = entriesWithImage > 0
                ? totalPngBytes / entriesWithImage / 1024
                : 0;
            Debug.WriteLine(
                $"[PDFium] _pageItems per-page avg: raw {avgBitmapKB} KB, " +
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

        // Height always derives from the page's point-space aspect ratio —
        // NOT from bitmap pixels.  High-res renders round pixel dimensions,
        // which would otherwise give placeholders and high-res items
        // slightly different heights and make scroll-offset estimates drift
        // cumulatively over many pages.
        double HeightFor(int pageIndex, double width)
        {
            var (w, h) = _pageSizes[pageIndex];
            return width * (h / w);
        }

        lock (_cacheLock)
        {
            // Every page shares one permanent PageImageItem (even pages
            // currently showing a high-res bitmap), so this reaches them all.
            foreach (var cacheEntry in _pageItems)
            {
                double scale = _pageSizes[cacheEntry.Key].Width / maxWidth;
                var item = cacheEntry.Value;
                item.Width = availableWidth * scale;
                item.ContainerWidth = _containerWidth;
                item.Height = HeightFor(cacheEntry.Key, item.Width);
            }
        }

        // No Reset — PageImageItem.PropertyChanged applies the new sizes in place.

        // Rebuild the cumulative top-offset table (DIP, unzoomed) used by
        // TOC navigation and the page indicator.  O(n), heights are
        // deterministic (point-space aspect), so the table is exact.
        var offsets = new double[_pageCount + 1];
        double acc = 0;
        for (int i = 0; i < _pageCount; i++)
        {
            offsets[i] = acc;
            double scale = _pageSizes[i].Width / maxWidth;
            acc += HeightFor(i, availableWidth * scale) + PageSpacingDips;
        }
        offsets[_pageCount] = acc;
        _pageTopOffsets = offsets;
    }

    /// <summary>Vertical spacing between pages — must match the ListViewItem bottom margin in XAML.</summary>
    public const double PageSpacingDips = 16;

    private double[] _pageTopOffsets = [];

    /// <summary>
    /// Returns the cached unzoomed DIP offset of the top of
    /// <paramref name="pageIndex"/> within the page stack.  Multiply by the
    /// current zoom factor to get a ScrollView vertical offset.
    /// </summary>
    public double GetPageTopOffset(int pageIndex)
        => (uint)pageIndex < (uint)_pageTopOffsets.Length ? _pageTopOffsets[pageIndex] : 0;

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

                // Already cached (e.g. re-queued by ReRenderCachedPages) — the
                // page item already shows this bitmap; just refresh the LRU.
                bool alreadyCached;
                lock (_cacheLock)
                {
                    alreadyCached = TouchHighResCache(index);
                    if (alreadyCached)
                        _inflight.Remove(index);
                }
                if (alreadyCached)
                    continue;

                // Phase 1 — background thread: PDFium render to raw bytes.
                // scale = dpi / 72 (e.g. 400/72 ≈ 5.56, 600/72 ≈ 8.33).
                int renderDpi = _currentRenderDpi;
                float scale = renderDpi / 72f;

                int width, height;
                byte[] pixels;
                try
                {
                    (width, height, pixels) = _pdfiumService.RenderPageToRawBuffer(
                        _filePath, index, scale);
                }
                catch (Exception ex)
                {
                    // A failed render must not kill the loop or strand the
                    // index in _inflight (that page would stay blurry forever).
                    Debug.WriteLine($"[PDFium] High-res render failed for page {index}: {ex.Message}");
                    lock (_cacheLock)
                        _inflight.Remove(index);
                    continue;
                }

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
    /// Moves <paramref name="index"/> to the LRU tail.  Returns false when the
    /// index is not cached.  Caller must hold <see cref="_cacheLock"/>.
    /// </summary>
    private bool TouchHighResCache(int index)
    {
        for (var node = _highResCache.First; node != null; node = node.Next)
        {
            if (node.Value.Key == index)
            {
                var value = node.Value;
                _highResCache.Remove(node);
                _highResCache.AddLast(value);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Called on the UI thread: creates a <see cref="WriteableBitmap"/> from the
    /// raw buffer and swaps it into the page's existing <see cref="PageImageItem"/>
    /// via the <c>Image</c> property.  No collection notification — the ListView
    /// keeps its container and only the Image element's texture changes, so the
    /// placeholder stays on screen until the high-res bitmap is ready (no blank
    /// frame, no re-layout).
    /// </summary>
    private void FinalizeRender(int index, int width, int height, byte[] pixels, int renderDpi)
    {
        lock (_cacheLock)
            _inflight.Remove(index);

        if (renderDpi != _currentRenderDpi)
            return;

        // WriteableBitmap must be created on the UI thread — and we are on it.
        var bitmap = PDFiumService.CreateWriteableBitmapFromRawBuffer(width, height, pixels);

        List<int>? evictedPages = null;
        lock (_cacheLock)
        {
            // Add to tail (most recent).
            _highResCache.AddLast(new KeyValuePair<int, WriteableBitmap>(index, bitmap));

            // Evict oldest when over capacity.
            while (_highResCache.Count > MaxHighResCacheSize)
            {
                evictedPages ??= [];
                evictedPages.Add(_highResCache.First!.Value.Key);
                _highResCache.RemoveFirst();
            }
        }

        // Downgrade evicted pages back to their placeholder bitmaps, releasing
        // the big high-res textures.  The LRU head is the least-recently needed
        // page — normally far outside the viewport, so this is invisible.
        if (evictedPages != null)
        {
            foreach (int evicted in evictedPages)
            {
                if ((uint)evicted < (uint)_placeholderBitmaps.Length &&
                    _placeholderBitmaps[evicted] is { } placeholder &&
                    _pageItems.TryGetValue(evicted, out var evictedItem) &&
                    !ReferenceEquals(evictedItem.Image, placeholder))
                {
                    evictedItem.Image = placeholder;
                    PageBitmapChanged?.Invoke(this, evicted);
                }
            }
        }

        // In-place upgrade: same item instance, new Image value.
        if (_pageItems.TryGetValue(index, out var item))
            item.Image = bitmap;

        PageBitmapChanged?.Invoke(this, index);
    }

    /// <summary>
    /// Ensures a high-res render is queued for <paramref name="index"/>.
    /// (The page ListView's inner scrolling is disabled, so it has no viewport
    /// and no realization ever triggers a render — the host drives this from
    /// the ScrollView viewport instead.)
    /// Must be called on the UI thread.
    /// </summary>
    public void EnsureHighResRender(int index)
    {
        lock (_cacheLock)
        {
            // Already rendered — the page item already shows the bitmap;
            // just refresh the LRU position.
            if (TouchHighResCache(index))
                return;
        }

        EnqueueRender(index);
    }

    private void EnqueueRender(int index)
    {
        lock (_cacheLock)
        {
            if (!_inflight.Add(index))
                return; // already queued or rendering
        }

        if (!_renderChannel.Writer.TryWrite(index))
        {
            // Queue full — drop the request; the next viewport pass re-issues it.
            lock (_cacheLock)
                _inflight.Remove(index);
        }
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
    /// Returns the permanent page item for <paramref name="index"/> without any
    /// side effects — no render enqueue, no LRU manipulation.  Safe to call
    /// from hot paths such as hit‑testing.
    /// </summary>
    public PageImageItem? GetPageItem(int index)
    {
        _pageItems.TryGetValue(index, out var item);
        return item;
    }

    /// <summary>Underlying PDFium service — used by text selection layer.</summary>
    public PDFiumService PdfiumService => _pdfiumService;

    /// <summary>File path of the opened document.</summary>
    public string FilePath => _filePath;

    // ════════════════════════════════════════════════════════════
    //  IList — virtualizing access for the items control
    // ════════════════════════════════════════════════════════════

    public object? this[int index]
    {
        get
        {
            // Pure lookup — high-res rendering is driven from the viewport
            // (EnsureHighResRender), not from container realization.
            _pageItems.TryGetValue(index, out PageImageItem? item);
            return item;
        }
        set
        {
            // Items are never replaced — high-res upgrades are in-place
            // property updates on the existing PageImageItem.
        }
    }

    public int Count => _pageCount;

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
    //  Remaining IList members (unused by the items control)
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
