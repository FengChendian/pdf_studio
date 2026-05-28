using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using pdf_studio.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace pdf_studio.Views;

public sealed partial class HomePage : Page
{
    public event EventHandler? OpenFileRequested;
    public event EventHandler<string>? RecentFileClicked;
    public event EventHandler<string>? HistoryItemRemoved;

    private HistoryService? _historyService;

    public HomePage()
    {
        InitializeComponent();
    }

    public void SetHistoryService(HistoryService historyService)
    {
        _historyService = historyService;
        RefreshRecentFiles();
    }

    public void RefreshRecentFiles()
    {
        if (_historyService == null) return;

        var files = _historyService.GetRecentFiles();
        if (files.Count > 0)
        {
            RecentFilesListView.ItemsSource = new ObservableCollection<RecentFile>(files);
            RecentFilesSection.Visibility = Visibility.Visible;
        }
        else
        {
            RecentFilesSection.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenPdfButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RecentFilesListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentFile file)
        {
            RecentFileClicked?.Invoke(this, file.FilePath);
        }
    }

    private void RemoveHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filePath)
        {
            _historyService?.Remove(filePath);
            _ = _historyService!.SaveAsync();
            RefreshRecentFiles();
            HistoryItemRemoved?.Invoke(this, filePath);
        }
    }
}
