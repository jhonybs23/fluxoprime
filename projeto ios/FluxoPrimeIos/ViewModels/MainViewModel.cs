using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxoPrimeCore;
using FluxoPrimeMaui.Services;
using Microsoft.Maui.ApplicationModel;

namespace FluxoPrimeMaui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int CatalogPageSize = 60;

    private readonly ApiClient _api;
    private readonly ISessionService _sessionService;
    private readonly AppLibraryStore _libraryStore;
    private List<StreamItem> _allStreams = [];
    private List<StreamItem> _filteredStreams = [];
    private CancellationTokenSource? _searchCts;
    private int _visibleLimit = CatalogPageSize;

    [ObservableProperty]
    private string _section = "home";

    [ObservableProperty]
    private ObservableCollection<CategoryItem> _categories = [];

    [ObservableProperty]
    private ObservableCollection<CategoryItem> _categoryCards = [];

    [ObservableProperty]
    private ObservableCollection<StreamItem> _streams = [];

    [ObservableProperty]
    private CategoryItem? _selectedCategory;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _title = "Ao vivo";

    [ObservableProperty]
    private string _sectionLabel = "LIVE";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isCachedData;

    [ObservableProperty]
    private bool _isLiveSection;

    [ObservableProperty]
    private bool _isLiveCategoryHubVisible;

    [ObservableProperty]
    private bool _isLiveChannelListVisible;

    [ObservableProperty]
    private bool _isLiveGroupBackVisible;

    [ObservableProperty]
    private bool _isCatalogSection;

    [ObservableProperty]
    private bool _isHomeSection = true;

    [ObservableProperty]
    private bool _isContentSection;

    [ObservableProperty]
    private bool _isGridSection;

    [ObservableProperty]
    private bool _isLocalListSection;

    [ObservableProperty]
    private bool _hasCategories;

    [ObservableProperty]
    private bool _isCategoryStripVisible;

    [ObservableProperty]
    private bool _isContentSearchVisible = true;

    [ObservableProperty]
    private bool _isCategoryPanelOpen;

    [ObservableProperty]
    private string _categoryButtonText = "Categorias";

    [ObservableProperty]
    private string _categorySummaryText = "Todas as categorias";

    [ObservableProperty]
    private string _categoryPanelTitle = "Categorias";

    [ObservableProperty]
    private string _categoryPanelSubtitle = "Escolha uma lista";

    [ObservableProperty]
    private bool _canLoadMore;

    [ObservableProperty]
    private ObservableCollection<StreamItem> _recentStreams = [];

    [ObservableProperty]
    private ObservableCollection<StreamItem> _favoriteStreams = [];

    [ObservableProperty]
    private bool _hasRecentStreams;

    [ObservableProperty]
    private bool _hasFavoriteStreams;

    public MainViewModel(ApiClient api, ISessionService sessionService, AppLibraryStore libraryStore)
    {
        _api = api;
        _sessionService = sessionService;
        _libraryStore = libraryStore;
        RefreshHomeLists();
    }

    partial void OnSearchTextChanged(string value)
    {
        DebounceSearch();
    }

    [RelayCommand]
    private async Task LoadDataAsync()
    {
        if (IsHomeSection)
        {
            RefreshHomeLists();
            return;
        }

        IsLoading = true;
        StatusText = "Carregando...";
        try
        {
            _visibleLimit = CatalogPageSize;
            var cats = await _api.CategoriesAsync(Section);
            cats.Insert(0, new CategoryItem { Id = 0, Name = GetAllCategoryName() });
            Categories = new ObservableCollection<CategoryItem>(cats);
            CategoryCards = new ObservableCollection<CategoryItem>(cats.Where(item => item.Id != 0));
            HasCategories = Categories.Count > 1;
            SelectedCategory = Categories.FirstOrDefault();
            UpdateCategoryText();

            _allStreams = await _api.StreamsAsync(Section);
            IsCachedData = _api.LastRequestFromCache;
            ApplyFilters(resetItems: true);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "LoadData");
            StatusText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SelectSectionAsync(string section)
    {
        if (string.IsNullOrWhiteSpace(section) || Section == section)
        {
            return;
        }

        Section = section;
        Title = section switch
        {
            "vod" => "Filmes",
            "series" => "Series",
            _ => "Ao vivo"
        };
        SectionLabel = section switch
        {
            "vod" => "FILMES",
            "series" => "SERIES",
            _ => "LIVE"
        };
        IsLiveSection = section == "live";
        IsLiveChannelListVisible = IsLiveSection;
        IsLiveCategoryHubVisible = false;
        IsLiveGroupBackVisible = false;
        IsCatalogSection = !IsLiveSection;
        IsLocalListSection = false;
        IsGridSection = IsCatalogSection;
        IsHomeSection = false;
        IsContentSection = true;
        IsCategoryPanelOpen = false;
        SearchText = "";
        await LoadDataAsync();
    }

    [RelayCommand]
    private void ShowHome()
    {
        Section = "home";
        Title = "Inicio";
        SectionLabel = "HOME";
        StatusText = "";
        SearchText = "";
        Categories = [];
        CategoryCards = [];
        Streams = [];
        _allStreams = [];
        _filteredStreams = [];
        IsLiveSection = false;
        IsLiveCategoryHubVisible = false;
        IsLiveChannelListVisible = false;
        IsLiveGroupBackVisible = false;
        IsCatalogSection = false;
        IsGridSection = false;
        IsLocalListSection = false;
        IsHomeSection = true;
        IsContentSection = false;
        HasCategories = false;
        IsCategoryStripVisible = false;
        IsContentSearchVisible = false;
        IsCategoryPanelOpen = false;
        CategoryButtonText = "Categorias";
        CategorySummaryText = "Escolha uma area";
        CategoryPanelTitle = "Categorias";
        CategoryPanelSubtitle = "Escolha uma lista";
        CanLoadMore = false;
        RefreshHomeLists();
    }

    [RelayCommand]
    private void ShowFavorites()
    {
        ShowLocalList("favorites", "Favoritos", "FAVORITOS", _libraryStore.GetFavorites());
    }

    [RelayCommand]
    private void ShowHistory()
    {
        ShowLocalList("history", "Historico", "HISTORICO", _libraryStore.GetRecent());
    }

    [RelayCommand]
    private void FilterByCategory(CategoryItem? category)
    {
        SelectedCategory = category;
        _visibleLimit = CatalogPageSize;
        IsCategoryPanelOpen = false;
        UpdateCategoryText();
        ApplyFilters(resetItems: true);
    }

    [RelayCommand]
    private void ShowLiveGroups()
    {
        if (!IsLiveSection || !HasCategories)
        {
            return;
        }

        SelectedCategory = Categories.FirstOrDefault(item => item.Id == 0);
        SearchText = "";
        _visibleLimit = CatalogPageSize;
        UpdateCategoryText();
        ApplyFilters(resetItems: true);
    }

    [RelayCommand]
    private void OpenCategoryPanel()
    {
        if (!HasCategories) return;
        IsCategoryPanelOpen = true;
    }

    [RelayCommand]
    private void CloseCategoryPanel()
    {
        IsCategoryPanelOpen = false;
    }

    [RelayCommand]
    private async Task SelectStreamAsync(StreamItem item)
    {
        if (item.Id == 0) return;
        if (item.Type == "live" || Section == "history" || item.IsEpisode)
        {
            _libraryStore.AddRecent(item);
        }

        var navParams = new Dictionary<string, object> { { "stream", item } };
        var route = item.Type == "live" || item.IsEpisode || Section == "history"
            ? "player"
            : "details";
        await Shell.Current.GoToAsync(route, navParams);
    }

    [RelayCommand]
    private void LoadMore()
    {
        _visibleLimit += CatalogPageSize;
        AppendVisibleItems();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await _sessionService.ClearSessionAsync();
        await Shell.Current.GoToAsync("//login");
    }

    private void DebounceSearch()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                if (cts.IsCancellationRequested) return;
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    _visibleLimit = CatalogPageSize;
                    ApplyFilters(resetItems: true);
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);
    }

    private void ApplyFilters(bool resetItems)
    {
        var categoryId = SelectedCategory?.Id ?? 0;
        var query = SearchText.Trim();
        _filteredStreams = _allStreams
            .Where(item => (categoryId == 0 || item.Category == categoryId)
                && (string.IsNullOrWhiteSpace(query) || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var visible = IsCatalogSection
            ? _filteredStreams.Take(_visibleLimit).ToList()
            : _filteredStreams;

        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].DisplayIndex = i + 1;
        }

        if (resetItems || !IsCatalogSection)
        {
            Streams = new ObservableCollection<StreamItem>(visible);
        }
        else
        {
            AppendVisibleItems();
            return;
        }

        CanLoadMore = IsCatalogSection && _filteredStreams.Count > visible.Count;
        UpdateContentVisibility();
        StatusText = IsCatalogSection
            ? $"{visible.Count} de {_filteredStreams.Count} carregados"
            : IsLocalListSection
                ? $"{_filteredStreams.Count} itens"
                : $"{_filteredStreams.Count} canais";
    }

    private void AppendVisibleItems()
    {
        if (!IsCatalogSection)
        {
            ApplyFilters(resetItems: true);
            return;
        }

        var targetCount = Math.Min(_visibleLimit, _filteredStreams.Count);
        var currentCount = Streams.Count;
        if (targetCount <= currentCount)
        {
            CanLoadMore = _filteredStreams.Count > currentCount;
            UpdateContentVisibility();
            StatusText = $"{currentCount} de {_filteredStreams.Count} carregados";
            return;
        }

        for (var i = currentCount; i < targetCount; i++)
        {
            _filteredStreams[i].DisplayIndex = i + 1;
            Streams.Add(_filteredStreams[i]);
        }

        CanLoadMore = _filteredStreams.Count > Streams.Count;
        UpdateContentVisibility();
        StatusText = $"{Streams.Count} de {_filteredStreams.Count} carregados";
    }

    public void RefreshFromStore()
    {
        RefreshHomeLists();
        if (Section == "favorites")
        {
            ShowLocalList("favorites", "Favoritos", "FAVORITOS", _libraryStore.GetFavorites());
        }
        else if (Section == "history")
        {
            ShowLocalList("history", "Historico", "HISTORICO", _libraryStore.GetRecent());
        }
    }

    private void ShowLocalList(string section, string title, string label, IReadOnlyList<StreamItem> items)
    {
        Section = section;
        Title = title;
        SectionLabel = label;
        SearchText = "";
        Categories = [];
        CategoryCards = [];
        SelectedCategory = null;
        HasCategories = false;
        IsCategoryStripVisible = false;
        IsContentSearchVisible = true;
        IsCategoryPanelOpen = false;
        CategoryButtonText = "Categorias";
        CategorySummaryText = "Sem subcategorias";
        CategoryPanelTitle = "Categorias";
        CategoryPanelSubtitle = title;
        _allStreams = items.ToList();
        IsLiveSection = false;
        IsLiveCategoryHubVisible = false;
        IsLiveChannelListVisible = false;
        IsLiveGroupBackVisible = false;
        IsCatalogSection = false;
        IsLocalListSection = true;
        IsGridSection = true;
        IsHomeSection = false;
        IsContentSection = true;
        CanLoadMore = false;
        ApplyFilters(resetItems: true);
    }

    private void RefreshHomeLists()
    {
        var recent = _libraryStore.GetRecent().Take(12).ToList();
        var favorites = _libraryStore.GetFavorites().Take(12).ToList();
        RecentStreams = new ObservableCollection<StreamItem>(recent);
        FavoriteStreams = new ObservableCollection<StreamItem>(favorites);
        HasRecentStreams = recent.Count > 0;
        HasFavoriteStreams = favorites.Count > 0;
    }

    private void UpdateCategoryText()
    {
        CategoryPanelTitle = Section switch
        {
            "live" => "Grupos de canais",
            "vod" => "Categorias de filmes",
            "series" => "Categorias de series",
            _ => "Categorias"
        };
        CategoryPanelSubtitle = Title;
        CategoryButtonText = Section switch
        {
            "live" => "Grupos",
            "vod" => "Filtrar",
            "series" => "Filtrar",
            _ => "Categorias"
        };

        var name = SelectedCategory?.Name;
        if (string.IsNullOrWhiteSpace(name) || SelectedCategory?.Id == 0)
        {
            CategorySummaryText = GetAllCategoryName();
            return;
        }

        CategorySummaryText = name;
    }

    private void UpdateContentVisibility()
    {
        var selectedCategoryId = SelectedCategory?.Id ?? 0;
        IsLiveCategoryHubVisible = IsLiveSection
            && HasCategories
            && selectedCategoryId == 0
            && string.IsNullOrWhiteSpace(SearchText);
        IsLiveChannelListVisible = IsLiveSection && !IsLiveCategoryHubVisible;
        IsLiveGroupBackVisible = IsLiveChannelListVisible
            && HasCategories
            && (selectedCategoryId != 0 || !string.IsNullOrWhiteSpace(SearchText));
        IsCategoryStripVisible = HasCategories && !IsLiveSection && !IsLiveCategoryHubVisible;
        IsContentSearchVisible = IsContentSection && !IsLiveCategoryHubVisible;
    }

    private string GetAllCategoryName()
    {
        return Section switch
        {
            "live" => "Todos os canais",
            "vod" => "Todos os filmes",
            "series" => "Todas as series",
            _ => "Todas as categorias"
        };
    }
}
