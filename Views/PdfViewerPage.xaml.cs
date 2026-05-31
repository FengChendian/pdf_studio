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

    private bool _sizesCalculated = false;

    private void PdfScrollViewer_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        if (!_sizesCalculated && e.NewSize.Width > 0)
        {
            _sizesCalculated = true;
            double availableWidth = e.NewSize.Width - 64; // ItemsRepeater margin (32 * 2)
            if (availableWidth > 0)
            {
                VirtualizingImages.CalculateDisplaySizes(availableWidth);
            }
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
    private volatile int _currentRenderDpi = 300;
    private readonly Dictionary<int, (double Width, double Height)> _pageSizes = [];
    private double _normalizedHeight;
    private readonly Dictionary<int, double> _displayWidths = [];
    private double _containerWidth;
    private readonly HashSet<int> _pendingRequests = [];
    private readonly RenderingService _renderingService;

    // 后台渲染线程
    private readonly Queue<int> _renderQueue = new();
    private readonly object _queueLock = new();
    private readonly Thread _renderThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherQueue _dispatcher;
    private bool _disposed;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public ImagesVirtualizingCollection(string path)
    {
        _renderingService = new RenderingService(path);
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("ImagesVirtualizingCollection 必须在 UI 线程创建");

        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = $"pdf-render-{System.IO.Path.GetFileNameWithoutExtension(path)}"
        };
        _renderThread.Start();
    }

    internal async Task InitializePlaceholdersAsync()
    {
        for (int i = 0; i < _renderingService.PageCount; i++)
        {
            _pageSizes[i] = _renderingService.GetPageSize(i);
            var bitmap = await _renderingService.RenderPageToBitmap(i, 72);
            if (bitmap != null)
            {
                _placeHolderCache[i] = new PageImageItem { Image = bitmap };
            }
        }
    }

    public void CalculateDisplaySizes(double availableWidth)
    {
        if (_pageSizes.Count == 0) return;

        _containerWidth = availableWidth;
        double maxAspectRatio = 0;
        foreach (var (w, h) in _pageSizes.Values)
        {
            double ratio = w / h;
            if (ratio > maxAspectRatio) maxAspectRatio = ratio;
        }

        _normalizedHeight = availableWidth / maxAspectRatio;

        foreach (var kvp in _pageSizes)
        {
            double displayWidth = _normalizedHeight * (kvp.Value.Width / kvp.Value.Height);
            _displayWidths[kvp.Key] = displayWidth;
        }

        lock (_cacheLock)
        {
            foreach (var kvp in _placeHolderCache)
            {
                kvp.Value.ContainerWidth = _containerWidth;
                if (_displayWidths.TryGetValue(kvp.Key, out var w))
                {
                    kvp.Value.Width = w;
                    kvp.Value.Height = _normalizedHeight;
                }
            }
            for (var node = _highResCache.First; node != null; node = node.Next)
            {
                node.Value.Value.ContainerWidth = _containerWidth;
                if (_displayWidths.TryGetValue(node.Value.Key, out var w))
                {
                    node.Value.Value.Width = w;
                    node.Value.Value.Height = _normalizedHeight;
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
            _pendingRequests.Remove(index);
            EnqueueRender(index);
        }
    }

    // --- 后台渲染线程 ---

    private void RenderLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            int index;
            lock (_queueLock)
            {
                while (_renderQueue.Count == 0 && !_cts.Token.IsCancellationRequested)
                {
                    Monitor.Wait(_queueLock);
                }
                if (_cts.Token.IsCancellationRequested) break;
                // 渲染前裁剪队列：保留最新 5 个
                while (_renderQueue.Count > 5)
                {
                    var discarded = _renderQueue.Dequeue();
                    _dispatcher.TryEnqueue(() => _pendingRequests.Remove(discarded));
                }
                index = _renderQueue.Dequeue();
            }

            // CPU 密集型：后台线程以当前 DPI 同步渲染
            int renderDpi = _currentRenderDpi;
            var pngBytes = _renderingService.RenderPageToBytes(index, renderDpi);
            if (pngBytes != null)
            {
                int capturedDpi = renderDpi;
                _dispatcher.TryEnqueue(() => _ = FinalizeRenderAsync(index, pngBytes, capturedDpi));
            }
            else
            {
                _dispatcher.TryEnqueue(() => _pendingRequests.Remove(index));
            }
        }
    }

    private async Task FinalizeRenderAsync(int index, byte[] pngBytes, int renderDpi)
    {
        if (renderDpi != _currentRenderDpi)
        {
            _pendingRequests.Remove(index);
            return;
        }

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
            Height = _displayWidths.Count > 0 ? _normalizedHeight : double.NaN,
            ContainerWidth = _displayWidths.Count > 0 ? _containerWidth : double.NaN
        };

        lock (_cacheLock)
        {
            // 移除同一 key 的旧节点（如果存在）
            var node = _highResCache.First;
            while (node != null)
            {
                if (node.Value.Key == index)
                {
                    _highResCache.Remove(node);
                    break;
                }
                node = node.Next;
            }

            // 容量满时淘汰最老的
            while (_highResCache.Count >= MaxHighResCacheSize)
            {
                _highResCache.RemoveFirst();
            }

            // 添加到队尾（最新）
            _highResCache.AddLast(new KeyValuePair<int, PageImageItem>(index, item));
        }
        _pendingRequests.Remove(index);

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
        lock (_queueLock)
        {
            _pendingRequests.Add(index);
            _renderQueue.Enqueue(index);
            Monitor.Pulse(_queueLock);
        }
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
        lock (_queueLock) { Monitor.PulseAll(_queueLock); }
        _renderThread.Join(TimeSpan.FromSeconds(5));
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
