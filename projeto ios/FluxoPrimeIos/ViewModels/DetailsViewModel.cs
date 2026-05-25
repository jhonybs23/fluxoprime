using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxoPrimeCore;
using FluxoPrimeMaui.Services;
using Microsoft.Maui.Storage;

namespace FluxoPrimeMaui.ViewModels;

[QueryProperty(nameof(Stream), "stream")]
public partial class DetailsViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly AppLibraryStore _libraryStore;

    [ObservableProperty]
    private StreamItem _stream = new();

    [ObservableProperty]
    private MovieInfo? _movieInfo;

    [ObservableProperty]
    private SeriesInfo? _seriesInfo;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _detailsText = "";

    [ObservableProperty]
    private string _plotText = "";

    [ObservableProperty]
    private bool _hasPlot;

    [ObservableProperty]
    private bool _isPlayable;

    [ObservableProperty]
    private bool _isSeries;

    [ObservableProperty]
    private string _coverImage = "";

    [ObservableProperty]
    private ObservableCollection<SeriesSeason> _seasonOptions = [];

    [ObservableProperty]
    private SeriesSeason? _selectedSeason;

    [ObservableProperty]
    private ObservableCollection<SeriesEpisode> _selectedEpisodes = [];

    [ObservableProperty]
    private SeriesEpisode? _continueEpisode;

    [ObservableProperty]
    private bool _hasContinueEpisode;

    [ObservableProperty]
    private ObservableCollection<StreamItem> _recommendedStreams = [];

    [ObservableProperty]
    private bool _hasRecommendations;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private string _favoriteButtonText = "Favoritar";

    public DetailsViewModel(ApiClient api, AppLibraryStore libraryStore)
    {
        _api = api;
        _libraryStore = libraryStore;
    }

    partial void OnSelectedSeasonChanged(SeriesSeason? value)
    {
        RefreshEpisodeStates();
        SelectedEpisodes = new ObservableCollection<SeriesEpisode>(value?.Episodes ?? []);
    }

    public async Task InitializeAsync()
    {
        if (Stream.Id == 0) return;

        CoverImage = Stream.Icon;
        PlotText = "";
        HasPlot = false;
        RecommendedStreams = [];
        HasRecommendations = false;
        IsPlayable = Stream.Type == "movie";
        IsSeries = Stream.Type == "series";
        IsFavorite = _libraryStore.IsFavorite(Stream);
        FavoriteButtonText = IsFavorite ? "Favorito" : "Favoritar";
        IsLoading = true;
        try
        {
            if (Stream.Type == "movie")
            {
                MovieInfo = await _api.VodInfoAsync(Stream);
                DetailsText = BuildMovieMetaText(MovieInfo);
                SetPlot(MovieInfo.Plot);
                await LoadRecommendationsAsync("vod");
            }
            else if (Stream.Type == "series")
            {
                SeriesInfo = await _api.SeriesInfoAsync(Stream);
                CoverImage = string.IsNullOrWhiteSpace(SeriesInfo.Cover) ? Stream.Icon : SeriesInfo.Cover;
                DetailsText = BuildSeriesMetaText(SeriesInfo);
                SetPlot(SeriesInfo.Plot);
                BindSeriesNavigation(SeriesInfo);
                await LoadRecommendationsAsync("series");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "LoadDetails");
            DetailsText = "Nao foi possivel carregar os detalhes.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (Stream.Id == 0) return;
        _libraryStore.AddRecent(Stream);
        var navParams = new Dictionary<string, object> { { "stream", Stream } };
        await Shell.Current.GoToAsync("player", navParams);
    }

    [RelayCommand]
    private async Task PlayEpisodeAsync(SeriesEpisode episode)
    {
        if (episode.Id == 0) return;
        SaveLastEpisode(Stream.Id, episode);
        var episodeStream = new StreamItem
        {
            Id = episode.Id,
            Name = episode.DisplayTitle,
            Icon = episode.Icon,
            Type = "series",
            IsEpisode = true
        };
        _libraryStore.AddRecent(episodeStream);
        var navParams = new Dictionary<string, object> { { "stream", episodeStream } };
        await Shell.Current.GoToAsync("player", navParams);
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (Stream.Id == 0) return;
        IsFavorite = _libraryStore.ToggleFavorite(Stream);
        FavoriteButtonText = IsFavorite ? "Favorito" : "Favoritar";
    }

    [RelayCommand]
    private async Task SelectRecommendationAsync(StreamItem item)
    {
        if (item.Id == 0) return;
        var navParams = new Dictionary<string, object> { { "stream", item } };
        await Shell.Current.GoToAsync("details", navParams);
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        await Shell.Current.GoToAsync("..");
    }

    private void BindSeriesNavigation(SeriesInfo info)
    {
        SeasonOptions = new ObservableCollection<SeriesSeason>(info.Seasons);
        ContinueEpisode = LoadLastEpisode(info);
        HasContinueEpisode = ContinueEpisode is not null;
        RefreshEpisodeStates(info);

        SelectedSeason = ContinueEpisode is not null
            ? info.Seasons.FirstOrDefault(season => season.Number == ContinueEpisode.Season) ?? info.Seasons.FirstOrDefault()
            : info.Seasons.FirstOrDefault();
    }

    private async Task LoadRecommendationsAsync(string section)
    {
        try
        {
            var items = await _api.StreamsAsync(section);
            var sameCategory = items
                .Where(item => item.Id != Stream.Id && item.Category == Stream.Category)
                .Take(12)
                .ToList();

            if (sameCategory.Count < 8)
            {
                sameCategory = sameCategory
                    .Concat(items.Where(item => item.Id != Stream.Id && item.Category != Stream.Category).Take(12 - sameCategory.Count))
                    .GroupBy(item => item.Id)
                    .Select(group => group.First())
                    .Take(12)
                    .ToList();
            }

            RecommendedStreams = new ObservableCollection<StreamItem>(sameCategory);
            HasRecommendations = RecommendedStreams.Count > 0;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "LoadRecommendations");
            RecommendedStreams = [];
            HasRecommendations = false;
        }
    }

    private void SetPlot(string value)
    {
        PlotText = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        HasPlot = !string.IsNullOrWhiteSpace(PlotText);
    }

    private static string BuildMovieMetaText(MovieInfo info)
    {
        var parts = new[]
        {
            Meta("Genero", info.Genre),
            Meta("Ano", info.ReleaseDate),
            Meta("Duracao", CleanDuration(info.Duration)),
            Meta("Nota", CleanRating(info.Rating)),
            Meta("Diretor", CleanPeople(info.Director)),
            Meta("Elenco", CleanPeople(info.Cast)),
        };
        return string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildSeriesMetaText(SeriesInfo info)
    {
        var parts = new[]
        {
            Meta("Genero", info.Genre),
            Meta("Ano", info.ReleaseDate),
            Meta("Nota", CleanRating(info.Rating)),
            Meta("Diretor", CleanPeople(info.Director)),
            Meta("Elenco", CleanPeople(info.Cast)),
        };
        return string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string Meta(string label, string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : $"{label}: {value}";
    }

    private SeriesEpisode? LoadLastEpisode(SeriesInfo info)
    {
        var id = Preferences.Default.Get(LastEpisodeKey(Stream.Id, "id"), 0);
        if (id <= 0) return null;
        return info.Seasons
            .SelectMany(season => season.Episodes)
            .FirstOrDefault(episode => episode.Id == id);
    }

    private static void SaveLastEpisode(int seriesId, SeriesEpisode episode)
    {
        if (seriesId <= 0 || episode.Id <= 0) return;
        Preferences.Default.Set(LastEpisodeKey(seriesId, "id"), episode.Id);
        Preferences.Default.Set(LastEpisodeKey(seriesId, "season"), episode.Season);
        Preferences.Default.Set(LastEpisodeKey(seriesId, "episode"), episode.Episode);
        var watched = LoadWatchedIds(seriesId);
        watched.Add(episode.Id);
        Preferences.Default.Set(WatchedEpisodesKey(seriesId), string.Join(",", watched.Order()));
    }

    private static string LastEpisodeKey(int seriesId, string field) => $"series:last:{seriesId}:{field}";

    private void RefreshEpisodeStates(SeriesInfo? info = null)
    {
        info ??= SeriesInfo;
        if (info is null || Stream.Id <= 0) return;

        var currentId = Preferences.Default.Get(LastEpisodeKey(Stream.Id, "id"), 0);
        var watched = LoadWatchedIds(Stream.Id);
        foreach (var episode in info.Seasons.SelectMany(season => season.Episodes))
        {
            episode.IsCurrent = episode.Id == currentId;
            episode.IsWatched = watched.Contains(episode.Id);
        }
    }

    private static HashSet<int> LoadWatchedIds(int seriesId)
    {
        var raw = Preferences.Default.Get(WatchedEpisodesKey(seriesId), "");
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();
    }

    private static string WatchedEpisodesKey(int seriesId) => $"series:watched:{seriesId}";

    private static string CleanDuration(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var trimmed = value.Trim();
        return trimmed is "0" or "00:00" or "00:00:00" ? "" : trimmed;
    }

    private static string CleanRating(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var trimmed = value.Trim();
        return trimmed is "0" or "0.0" or "0.00" ? "" : trimmed;
    }

    private static string CleanPeople(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var trimmed = value.Trim();
        var letters = trimmed.Count(char.IsLetter);
        if (letters == 0) return "";
        var latinLetters = trimmed.Count(ch => ch <= '\u024F' && char.IsLetter(ch));
        return latinLetters * 2 < letters ? "" : trimmed;
    }
}
