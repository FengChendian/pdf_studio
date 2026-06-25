using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using pdf_studio.Services;

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
        Debug.WriteLine($"zoom: {PdfScrollViewer.ZoomFactor}");
        if (shouldBeHighDpi != _isHighDpiMode)
        {
            _isHighDpiMode = shouldBeHighDpi;
            int newDpi = shouldBeHighDpi ? HighRenderDpi : DefaultRenderDpi;
            VirtualizingImages.SetRenderDpi(newDpi);
        }
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
        int[] allPages = Enumerable.Range(0, _pageCount).ToArray();

        // Phase 1 — background thread: parallel PDFium render to raw bytes.
        var rawBuffers = await Task.Run(() =>
            _pdfiumService.RenderPagesToRawBuffers(_filePath, allPages, scale: 1f));

        // Phase 2 — UI thread: create WriteableBitmap from raw bytes.
        for (int i = 0; i < _pageCount; i++)
        {
            var (w, h, pixels) = rawBuffers[i];
            var bitmap = PDFiumService.CreateWriteableBitmapFromRawBuffer(w, h, pixels);
            _pageSizes[i] = (w, h);
            _placeHolderCache[i] = new PageImageItem { Image = bitmap };
        }

        Debug.WriteLine($"[PDFium] Batch-rendered {_pageCount} placeholder pages in parallel");
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
