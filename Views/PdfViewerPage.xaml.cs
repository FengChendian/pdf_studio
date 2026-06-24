using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using pdf_studio.Services;

namespace pdf_studio.Views;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage.Streams;

public sealed partial class PdfViewerPage : Page
{
    public readonly string FileName;
    //private readonly RenderingService _renderingService;

    public ObservableCollection<BitmapImage> Images { get; private set; } = [];
    public readonly ImagesVirtualizingCollection VirtualizingImages;

    // Zoom-DPI 阈值
    private const double ZoomThreshold = 2.0;
    private const int DefaultRenderDpi = 500;
    private const int HighRenderDpi = 600;
    private bool _isHighDpiMode = false;
    private DispatcherTimer? _zoomDebounceTimer;

    public int RenderedPageCount
    {
        get => Images.Count;
    }

    public PdfViewerPage(string filename)
    {
        FileName = filename;
        //_renderingService = new RenderingService(FileName);
        VirtualizingImages = new ImagesVirtualizingCollection(FileName);
        InitializeComponent();
        //InitRepeater();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        // 第一步：渲染所有 72dpi 占位图
        await VirtualizingImages.InitializePlaceholdersAsync();

        // 占位图就绪，切换可见性
        LoadingOverlay.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        PdfScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

        //RenderPages();
    }

    private void PdfScrollViewer_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        double availableWidth = e.NewSize.Width - 64; // ItemsRepeater margin (32 * 2)
        if (availableWidth > 0)
        {
            VirtualizingImages.CalculateDisplaySizes(availableWidth);
        }
    }

    private void PdfScrollViewer_ViewChanged(ScrollView sender, object args)
    {
        //Debug.WriteLine($"ViewChanged, vertical offset {sender.VerticalOffset}");
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

public class PageImageItem
{
    public BitmapImage? Image { get; set; }
    public double Width { get; set; } = double.NaN;
    public double Height { get; set; } = double.NaN;
    public double ContainerWidth { get; set; } = double.NaN;
}

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
    private readonly RenderingService _renderingService;

    // 后台渲染任务：有界队列，满了自动丢弃最旧请求
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
        _renderingService = new RenderingService(path);
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("ImagesVirtualizingCollection 必须在 UI 线程创建");

        _renderTask = Task.Run(RenderLoopAsync);
    }

    internal async Task InitializePlaceholdersAsync()
    {
        int pageCount = _renderingService.PageCount;

        // Step 1: Get page sizes from local RenderingService (fast, no rendering)
        for (int i = 0; i < pageCount; i++)
        {
            _pageSizes[i] = _renderingService.GetPageSize(i);
        }

        // Step 2: Try multi-process rendering first
        bool multiProcessSucceeded = false;

        try
        {
            using var server = new MultiProcessRenderServer(_filePath, workerCount: 8);
            var result = await server.RenderAllPagesAsync(dpi: 72);

            if (result.IsComplete)
            {
                for (int i = 0; i < pageCount; i++)
                {
                    var page = result.Pages[i];
                    var bitmap = await ConvertPngToBitmapAsync(page.PngBytes);
                    if (bitmap != null)
                    {
                        _placeHolderCache[i] = new PageImageItem { Image = bitmap };
                    }
                }
                multiProcessSucceeded = true;
                Debug.WriteLine($"[MultiProcess] Successfully rendered {pageCount} placeholder pages");
            }
        }
        catch (WorkerNotFoundException)
        {
            Debug.WriteLine("[MultiProcess] Worker executable not found, falling back to sequential rendering");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MultiProcess] Failed, falling back to sequential: {ex.Message}");
        }

        // Step 3: Fallback to sequential rendering if multi-process didn't succeed
        if (!multiProcessSucceeded)
        {
            for (int i = 0; i < pageCount; i++)
            {
                var bitmap = await _renderingService.RenderPageToBitmap(i, 72);
                if (bitmap != null)
                {
                    _placeHolderCache[i] = new PageImageItem { Image = bitmap };
                }
            }
        }
    }

    /// <summary>Converts PNG byte array to BitmapImage on the UI thread.</summary>
    private static async Task<BitmapImage?> ConvertPngToBitmapAsync(byte[] pngBytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            using (var stream = new InMemoryRandomAccessStream())
            {
                await stream.WriteAsync(pngBytes.AsBuffer());
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MultiProcess] Failed to create BitmapImage: {ex.Message}");
            return null;
        }
    }

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

    // --- 动态 DPI ---

    public void SetRenderDpi(int newDpi)
    {
        //if (newDpi == _currentRenderDpi)
        //    return;
        //Debug.WriteLine(newDpi);
        //int oldDpi = _currentRenderDpi;
        //_currentRenderDpi = newDpi;

        //if (newDpi > oldDpi)
        //{
        //    ReRenderCachedPages();
        //}
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

    // --- 后台渲染任务 ---

    private async Task RenderLoopAsync()
    {
        try
        {
            while (await _renderChannel.Reader.WaitToReadAsync(_cts.Token))
            {
                if (!_renderChannel.Reader.TryRead(out var index))
                    continue;

                // 渲染前检查高清缓存：已有则移到 LRU 末尾，跳过渲染
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
                    if (node != null) continue; // 缓存命中，跳过渲染
                }

                // CPU 密集型：后台线程以当前 DPI 同步渲染
                int renderDpi = _currentRenderDpi;
                var pngBytes = _renderingService.RenderPageToBytes(index, renderDpi);
                if (pngBytes != null)
                {
                    int capturedDpi = renderDpi;
                    _dispatcher.TryEnqueue(() => _ = FinalizeRenderAsync(index, pngBytes, capturedDpi));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
    }

    private async Task FinalizeRenderAsync(int index, byte[] pngBytes, int renderDpi)
    {
        if (renderDpi != _currentRenderDpi)
            return;

        var bitmap = new BitmapImage();
        using (var stream = new InMemoryRandomAccessStream())
        {
            await stream.WriteAsync(pngBytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }

        var item = new PageImageItem
        {
            Image = bitmap,
            Width = _displayWidths.GetValueOrDefault(index, double.NaN),
            ContainerWidth = _displayWidths.Count > 0 ? _containerWidth : double.NaN
        };

        lock (_cacheLock)
        {
            // 移除同一 key 的旧节点（如果存在）
            //var node = _highResCache.First;
            //while (node != null)
            //{
            //    if (node.Value.Key == index)
            //    {
            //        _highResCache.Remove(node);
            //        break;
            //    }
            //    node = node.Next;
            //}



            // 添加到队尾（最新）
            _highResCache.AddLast(new KeyValuePair<int, PageImageItem>(index, item));
            // 容量满时淘汰最老的
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

    // --- IList 核心实现 ---

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
            // ItemsRepeater 内部使用，通过 Replace 通知更新
        }
    }

    public int Count => _renderingService.PageCount;

    protected virtual void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        CollectionChanged?.Invoke(this, e);
    }

    // --- IDisposable ---

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _renderChannel.Writer.Complete();
        _renderTask.Wait(TimeSpan.FromSeconds(5));
        _cts.Dispose();
        _renderingService.Dispose();
    }

    // --- 其余 IList 接口 ---
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
