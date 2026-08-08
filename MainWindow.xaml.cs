using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using pdf_studio.Services;
using pdf_studio.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace pdf_studio;

public sealed partial class MainWindow : Window
{
    private HistoryService _historyService = new();
    private HomePage? _homePage;
    private TabViewItem? _homeTab;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon("Assets/icon.ico");
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        await _historyService.LoadAsync();
        CreateHomeTab();
    }

    private void CreateHomeTab()
    {
        _homePage = new HomePage();
        _homePage.SetHistoryService(_historyService);
        _homePage.OpenFileRequested += OnOpenFileRequested;
        _homePage.RecentFileClicked += OnRecentFileClicked;
        _homePage.HistoryItemRemoved += OnHistoryItemRemoved;

        _homeTab = new TabViewItem
        {
            Header = "Home",
            Content = _homePage,
            IsClosable = false,
            IconSource = new Microsoft.UI.Xaml.Controls.SymbolIconSource { Symbol = Symbol.Home },
        };

        MainTabView.TabItems.Add(_homeTab);
        MainTabView.SelectedItem = _homeTab;
    }

    private async void OnOpenFileRequested(object? sender, EventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            OpenPdfInTab(file.Path, file.Name);
        }
    }

    private void OnRecentFileClicked(object? sender, string filePath)
    {
        if (File.Exists(filePath))
        {
            var fileName = Path.GetFileName(filePath);
            OpenPdfInTab(filePath, fileName);
        }
        else
        {
            // File no longer exists — remove from history
            _historyService.Remove(filePath);
            _ = _historyService.SaveAsync();
            _homePage?.RefreshRecentFiles();
            ShowFileNotFoundDialog(filePath);
        }
    }

    private void OnHistoryItemRemoved(object? sender, string filePath)
    {
        // Check if any open tab references this file and close it
        var tabsToClose = MainTabView
            .TabItems.OfType<TabViewItem>()
            .Where(t => t.Tag is string tag && tag == filePath)
            .ToList();

        foreach (var tab in tabsToClose)
        {
            MainTabView.TabItems.Remove(tab);
        }
    }

    private void OpenPdfInTab(string filePath, string fileName)
    {
        // Check if file is already open in a tab
        var existingTab = MainTabView
            .TabItems.OfType<TabViewItem>()
            .FirstOrDefault(t => t.Tag is string tag && tag == filePath);

        if (existingTab != null)
        {
            MainTabView.SelectedItem = existingTab;
            return;
        }

        // Add to history
        _historyService.Add(filePath, fileName);
        _ = _historyService.SaveAsync();
        _homePage?.RefreshRecentFiles();

        // Create PDF viewer tab (page handles its own loading state)
        var pdfPage = new PdfViewerPage(filePath);

        var tabItem = new TabViewItem
        {
            Header = fileName,
            Content = pdfPage,
            Tag = filePath,
            IconSource = new Microsoft.UI.Xaml.Controls.SymbolIconSource
            {
                Symbol = Symbol.Document,
            },
        };

        // Mark the tab with an asterisk while there are unsaved highlights.
        pdfPage.DirtyStateChanged += (_, dirty) =>
        {
            tabItem.Header = dirty ? fileName + " *" : fileName;
        };

        MainTabView.TabItems.Add(tabItem);
        MainTabView.SelectedItem = tabItem;
    }

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab is not TabViewItem tab || !tab.IsClosable)
        {
            return;
        }

        if (tab.Content is PdfViewerPage pdfPage && pdfPage.HasUnsavedChanges)
        {
            var fileName = tab.Tag is string path
                ? Path.GetFileName(path)
                : tab.Header?.ToString() ?? string.Empty;

            var result = await ShowUnsavedHighlightsDialog(fileName);
            if (result == ContentDialogResult.Primary)
            {
                await pdfPage.SaveHighlightsAsync();
                // The page shows its own error dialog on failure; keep the
                // tab open if the highlights are still unsaved.
                if (pdfPage.HasUnsavedChanges)
                {
                    return;
                }
            }
            else if (result != ContentDialogResult.Secondary)
            {
                // Cancel — keep the tab open.
                return;
            }
        }

        MainTabView.TabItems.Remove(tab);
    }

    private async Task<ContentDialogResult> ShowUnsavedHighlightsDialog(string fileName)
    {
        var dialog = new ContentDialog
        {
            Title = "未保存的高亮",
            Content = $"“{fileName}” 中还有未保存的高亮。关闭前是否保存？",
            PrimaryButtonText = "保存",
            SecondaryButtonText = "不保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
        };
        return await dialog.ShowAsync();
    }

    private void OnAddTabButtonClick(TabView sender, object args)
    {
        // "+" button clicked — trigger file open
        OnOpenFileRequested(this, EventArgs.Empty);
    }

    private async void ShowFileNotFoundDialog(string filePath)
    {
        var dialog = new ContentDialog
        {
            Title = "File Not Found",
            Content = $"The file \"{filePath}\" no longer exists.",
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // ----- Drag & drop -----

    private static bool IsPdfItem(IStorageItem item) =>
        item is StorageFile file && file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private async void OnDragOver(object sender, DragEventArgs e)
    {
        // Inspecting the dragged items is async, so hold a deferral to keep
        // the drag operation alive while we check for PDF files.
        var deferral = e.GetDeferral();
        try
        {
            var hasPdf = false;
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                hasPdf = items.Any(IsPdfItem);
            }

            if (hasPdf)
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "打开 PDF";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
                DropOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                // Non-PDF content — refuse the drop and keep the overlay hidden.
                e.AcceptedOperation = DataPackageOperation.None;
                DropOverlay.Visibility = Visibility.Collapsed;
            }
            e.Handled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items.Where(IsPdfItem).Cast<StorageFile>())
        {
            if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
            {
                OpenPdfInTab(item.Path, item.Name);
                continue;
            }

            // Files dragged from virtual locations (zip archives, phones,
            // network stubs) have no local path — copy to a temp folder first.
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "pdf_studio", "dropped");
                Directory.CreateDirectory(tempDir);
                var tempFolder = await StorageFolder.GetFolderFromPathAsync(tempDir);
                var copy = await item.CopyAsync(tempFolder, item.Name, NameCollisionOption.GenerateUniqueName);
                OpenPdfInTab(copy.Path, copy.Name);
            }
            catch
            {
                // Best effort only — skip files that cannot be materialized locally.
            }
        }
    }
}
