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

public partial class ImagesVirtualizingCollection : IList, INotifyCollectionChanged, IDisposable
{
    private readonly Dictionary<int, BitmapImage> _placeHolderCache = [];
    private readonly LinkedList<KeyValuePair<int, BitmapImage>> _highResCache = new();
    private readonly object _cacheLock = new();
    private const int MaxHighResCacheSize = 5;
    private volatile int _currentRenderDpi = 300;
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
            var bitmap = await _renderingService.RenderPageToBitmap(i, 72);
            if (bitmap != null)
            {
                _placeHolderCache[i] = bitmap;
            }
        }
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
            _highResCache.AddLast(new KeyValuePair<int, BitmapImage>(index, bitmap));
        }
        _pendingRequests.Remove(index);

        _placeHolderCache.TryGetValue(index, out var oldValue);
        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Replace,
                bitmap,
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
                foreach (var kvp in _highResCache)
                {
                    if (kvp.Key == index)
                        return kvp.Value;
                }
            }

            EnqueueRender(index);

            _placeHolderCache.TryGetValue(index, out BitmapImage? placeHolderImage);
            return placeHolderImage;
        }
        set
        {
            if (value is BitmapImage bitmap && _placeHolderCache[index] != bitmap)
            {
                _placeHolderCache[index] = bitmap;
            }
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
