using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using pdf_studio.Services;
using pdf_studio.Views;
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
        AppWindow.SetIcon("Assets/light_icon.ico");
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

        MainTabView.TabItems.Add(tabItem);
        MainTabView.SelectedItem = tabItem;
    }

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab is TabViewItem tab && tab.IsClosable)
        {
            MainTabView.TabItems.Remove(tab);
        }
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
}
