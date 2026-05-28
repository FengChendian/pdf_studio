using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace pdf_studio.Services;

public class RecentFile
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime OpenedTime { get; set; }
}

public class HistoryService
{
    private const string HistoryFileName = "history.json";
    private const int MaxHistoryCount = 20;
    private List<RecentFile> _recentFiles = new();

    public HistoryService()
    {
    }

    public async Task LoadAsync()
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder;
            var file = await localFolder.TryGetItemAsync(HistoryFileName);
            if (file != null)
            {
                var json = await File.ReadAllTextAsync(file.Path);
                _recentFiles = JsonSerializer.Deserialize<List<RecentFile>>(json) ?? new List<RecentFile>();
            }
        }
        catch
        {
            _recentFiles = new List<RecentFile>();
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder;
            var filePath = Path.Combine(localFolder.Path, HistoryFileName);
            var json = JsonSerializer.Serialize(_recentFiles);
            await File.WriteAllTextAsync(filePath, json);
        }
        catch
        {
            // Silently fail — history is non-critical
        }
    }

    public void Add(string filePath, string fileName)
    {
        var existing = _recentFiles.FirstOrDefault(f =>
            string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            _recentFiles.Remove(existing);

        _recentFiles.Insert(0, new RecentFile
        {
            FilePath = filePath,
            FileName = fileName,
            OpenedTime = DateTime.Now
        });

        if (_recentFiles.Count > MaxHistoryCount)
            _recentFiles = _recentFiles.Take(MaxHistoryCount).ToList();
    }

    public void Remove(string filePath)
    {
        var item = _recentFiles.FirstOrDefault(f =>
            string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (item != null)
            _recentFiles.Remove(item);
    }

    public IReadOnlyList<RecentFile> GetRecentFiles() => _recentFiles.AsReadOnly();
}
