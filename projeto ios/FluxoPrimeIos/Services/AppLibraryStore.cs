using System.Text.Json;
using FluxoPrimeCore;

namespace FluxoPrimeMaui.Services;

public sealed class AppLibraryStore
{
    private const int MaxRecentItems = 30;
    private readonly string _libraryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluxoPrimeMaui",
        "library.json");

    public IReadOnlyList<StreamItem> GetRecent() => Load().Recent;

    public IReadOnlyList<StreamItem> GetFavorites() => Load().Favorites;

    public bool IsFavorite(StreamItem item)
    {
        if (item.Id <= 0) return false;
        return Load().Favorites.Any(existing => SameContent(existing, item));
    }

    public void AddRecent(StreamItem item)
    {
        if (item.Id <= 0) return;
        var data = Load();
        data.Recent.RemoveAll(existing => SameContent(existing, item));
        data.Recent.Insert(0, Snapshot(item));
        if (data.Recent.Count > MaxRecentItems)
        {
            data.Recent.RemoveRange(MaxRecentItems, data.Recent.Count - MaxRecentItems);
        }
        Save(data);
    }

    public bool ToggleFavorite(StreamItem item)
    {
        if (item.Id <= 0) return false;
        var data = Load();
        var existing = data.Favorites.FindIndex(saved => SameContent(saved, item));
        if (existing >= 0)
        {
            data.Favorites.RemoveAt(existing);
            Save(data);
            return false;
        }

        data.Favorites.Insert(0, Snapshot(item));
        Save(data);
        return true;
    }

    private LibraryData Load()
    {
        try
        {
            if (!File.Exists(_libraryFile)) return new LibraryData();
            var json = File.ReadAllText(_libraryFile);
            return JsonSerializer.Deserialize<LibraryData>(json, JsonOptions()) ?? new LibraryData();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Load local library");
            return new LibraryData();
        }
    }

    private void Save(LibraryData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_libraryFile)!);
            var json = JsonSerializer.Serialize(data, JsonOptions());
            File.WriteAllText(_libraryFile, json);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Save local library");
        }
    }

    private static StreamItem Snapshot(StreamItem item)
    {
        return new StreamItem
        {
            Id = item.Id,
            Name = item.Name,
            Icon = item.Icon,
            Category = item.Category,
            Type = item.Type,
            Rating = item.Rating,
            Year = item.Year,
            IsEpisode = item.IsEpisode,
        };
    }

    private static bool SameContent(StreamItem a, StreamItem b)
    {
        return a.Id == b.Id
            && string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase)
            && a.IsEpisode == b.IsEpisode;
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class LibraryData
    {
        public List<StreamItem> Recent { get; set; } = [];
        public List<StreamItem> Favorites { get; set; } = [];
    }
}
