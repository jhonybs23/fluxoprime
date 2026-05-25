using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Globalization;

namespace FluxoPrimeCore;

public sealed class ApiClient
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly ISessionService? _sessionService;
    private string _cacheDir;
    private string _csrf = "";

    public string BaseUrl { get; } = (Environment.GetEnvironmentVariable("FLUXOPRIME_API") ?? "https://desktop.fluxoprime.com").TrimEnd('/');
    public bool LastRequestFromCache { get; private set; }

    public ApiClient(ISessionService? sessionService = null)
    {
        _sessionService = sessionService;
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            AllowAutoRedirect = false,
        };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.4 LibVLC/3.0.4");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        
        _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxoPrimeMaui", "api-cache");
    }

    public async Task InitAsync()
    {
        using var res = await _http.GetAsync(BaseUrl + "/login");
        var html = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            var detail = string.IsNullOrWhiteSpace(html)
                ? $"HTTP {(int)res.StatusCode}"
                : ReadError(html, $"HTTP {(int)res.StatusCode}");
            throw new InvalidOperationException(
                $"API desktop nao respondeu o login ({detail}). Customize se o Nginx aponta para a pasta public e usa try_files para /index.php."
            );
        }

        var match = Regex.Match(html, "name=\"_csrf\"\\s+value=\"([^\"]+)\"");
        if (match.Success) _csrf = WebUtility.HtmlDecode(match.Groups[1].Value);
        if (string.IsNullOrWhiteSpace(_csrf))
        {
            throw new InvalidOperationException("Login da API abriu, mas o token CSRF nao veio. Customize a rota /login no servidor desktop.");
        }
    }

    public async Task LoginAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(_csrf)) await InitAsync();
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_csrf"] = _csrf,
            ["username"] = username,
            ["password"] = password,
        });
        body.Headers.ContentType!.CharSet = null;

        var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/login") { Content = body };
        req.Headers.Add("X-CSRF-Token", _csrf);
        var res = await _http.SendAsync(req);
        var json = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(json, "Falha no login."));
        
        if (_sessionService != null)
            await _sessionService.SaveSessionAsync(username, password, _csrf);
    }

    public async Task<bool> TryAutoLoginAsync()
    {
        if (_sessionService == null) return false;
        if (!await _sessionService.HasSessionAsync()) return false;
        var (user, pass, csrf) = await _sessionService.LoadSessionAsync();
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass)) return false;

        try
        {
            _csrf = csrf;
            await InitAsync();
            await LoginAsync(user, pass);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<UserMe> MeAsync() => await GetAsync<UserMe>("/api/me") ?? new UserMe();

    public async Task<List<CategoryItem>> CategoriesAsync(string section)
    {
        var path = section switch
        {
            "vod" => "/api/vod/categories",
            "series" => "/api/series/categories",
            _ => "/api/live/categories",
        };
        var items = await GetAsync<List<CategoryItem>>(path) ?? [];
        AppLog.Info($"Categorias {section}: {items.Count}");
        return items;
    }

    public async Task<List<StreamItem>> StreamsAsync(string section)
    {
        LastRequestFromCache = false;
        var path = section switch
        {
            "vod" => "/api/vod/streams",
            "series" => "/api/series",
            _ => "/api/live/streams",
        };
        var json = await GetRawAsync(path);
        var items = JsonSerializer.Deserialize<List<StreamItem>>(json, JsonOptions()) ?? [];
        if (items.Count == 0)
        {
            AppLog.Info($"Streams {section}: resposta vazia, tentando atualizar sem cache");
            json = await GetRawAsync($"{path}?desktop=1&refresh={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", logPath: path);
            items = JsonSerializer.Deserialize<List<StreamItem>>(json, JsonOptions()) ?? [];
        }
        if (items.Count == 0 && TryReadCachedJson(path, out var cachedJson))
        {
            LastRequestFromCache = true;
            AppLog.Info($"Streams {section}: usando cache local por resposta vazia");
            items = JsonSerializer.Deserialize<List<StreamItem>>(cachedJson, JsonOptions()) ?? [];
        }
        if (items.Count > 0)
        {
            WriteCachedJson(path, json);
        }
        foreach (var item in items)
        {
            item.Type = section switch { "vod" => "movie", "series" => "series", _ => "live" };
        }
        AppLog.Info($"Streams {section}: {items.Count}");
        return items;
    }

    public async Task<PlayResponse> PlayUrlAsync(StreamItem item, string? extensionOverride = null, bool refresh = false)
    {
        var type = item.Type == "movie" ? "movie" : item.Type == "series" ? "series" : "live";
        var ext = !string.IsNullOrWhiteSpace(extensionOverride) ? extensionOverride : type == "live" ? "ts" : "mp4";
        var url = $"{BaseUrl}/api/play?desktop=1&type={Uri.EscapeDataString(type)}&id={item.Id}&ext={ext}&name={Uri.EscapeDataString(item.Name)}";
        if (refresh)
        {
            url += $"&refresh={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        }
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-FluxoPrime-Desktop", "1");
        if (!string.IsNullOrWhiteSpace(_csrf)) req.Headers.Add("X-CSRF-Token", _csrf);
        var res = await _http.SendAsync(req);
        var json = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(json, "Falha ao gerar stream."));
        var play = JsonSerializer.Deserialize<PlayResponse>(json, JsonOptions()) ?? new PlayResponse();
        if (type == "live" && !string.IsNullOrWhiteSpace(extensionOverride))
        {
            var requestedExtension = NormalizeExtension(extensionOverride);
            var requestedCandidate = play.Candidates.FirstOrDefault(candidate =>
                NormalizeExtension(candidate.Extension) == requestedExtension &&
                !string.IsNullOrWhiteSpace(candidate.Url));

            if (requestedCandidate is not null)
            {
                play.Url = requestedCandidate.Url;
                play.Extension = requestedCandidate.Extension;
                play.Mime = requestedCandidate.Mime;
            }
        }

        play.Url = MakeAbsoluteUrl(play.Url);
        AppLog.Info($"PlayUrl {item.Type}/{item.Id}: ext={play.Extension}; {(string.IsNullOrWhiteSpace(play.Url) ? "vazio" : play.Url[..Math.Min(play.Url.Length, 80)])}");
        return play;
    }

    private static string NormalizeExtension(string value)
    {
        return value.Trim().TrimStart('.').ToLowerInvariant();
    }

    public async Task<MovieInfo> VodInfoAsync(StreamItem item)
    {
        var json = await GetRawAsync($"/api/vod/{item.Id}");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = root.TryGetProperty("info", out var infoElement) ? infoElement : root;
        var data = root.TryGetProperty("movie_data", out var dataElement) ? dataElement : root;

        return new MovieInfo
        {
            Title = FirstString(info, data, "name", "title", "o_name") ?? item.Name,
            Plot = FirstString(info, data, "plot", "description", "desc") ?? "",
            Genre = FirstString(info, data, "genre", "category") ?? "",
            Cast = FirstString(info, data, "cast", "actors") ?? "",
            Director = FirstString(info, data, "director") ?? "",
            Rating = FirstString(info, data, "rating", "rating_5based", "imdb_rating") ?? item.Rating,
            ReleaseDate = FirstString(info, data, "releasedate", "releaseDate", "release_date", "year") ?? item.Year,
            Duration = FirstString(info, data, "duration", "duration_secs", "duration_seconds", "runtime", "runtime_sec", "runtime_seconds", "length", "length_secs") ?? "",
            ImdbId = FirstString(info, data, "imdb_id", "imdb", "imdbid") ?? "",
            TmdbId = FirstString(info, data, "tmdb_id", "tmdb") ?? "",
        };
    }

    public async Task<SeriesInfo> SeriesInfoAsync(StreamItem item)
    {
        var json = await GetRawAsync($"/api/series/{item.Id}");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = root.TryGetProperty("info", out var infoElement) ? infoElement : root;
        var data = root.TryGetProperty("series_data", out var dataElement) ? dataElement : root;

        var series = new SeriesInfo
        {
            Title = FirstString(info, data, "name", "title", "o_name") ?? item.Name,
            Plot = FirstString(info, data, "plot", "description", "desc") ?? "",
            Genre = FirstString(info, data, "genre", "category") ?? "",
            Cast = FirstString(info, data, "cast", "actors") ?? "",
            Director = FirstString(info, data, "director") ?? "",
            Rating = FirstString(info, data, "rating", "rating_5based", "imdb_rating") ?? item.Rating,
            ReleaseDate = FirstString(info, data, "releasedate", "releaseDate", "release_date", "year") ?? item.Year,
            Cover = FirstString(info, data, "cover", "movie_image", "image", "poster", "backdrop_path") ?? item.Icon,
        };

        if (root.TryGetProperty("seasons", out var seasonsElement))
        {
            ParseSeriesSeasons(seasonsElement, series);
        }

        if (root.TryGetProperty("episodes", out var episodesElement))
        {
            ParseSeriesEpisodes(episodesElement, series, item);
        }

        foreach (var season in series.Seasons)
        {
            season.Episodes = season.Episodes
                .OrderBy(episode => episode.Episode == 0 ? int.MaxValue : episode.Episode)
                .ThenBy(episode => episode.Title)
                .ToList();
        }

        series.Seasons = series.Seasons
            .Where(season => season.Episodes.Count > 0)
            .OrderBy(season => season.Number == 0 ? int.MaxValue : season.Number)
            .ToList();

        return series;
    }

    public string CookieHeader()
    {
        var cookies = _cookies.GetCookies(new Uri(BaseUrl));
        return string.Join("; ", cookies.Cast<Cookie>().Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    private async Task<T?> GetAsync<T>(string path)
    {
        var json = await GetRawAsync(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions());
    }

    private async Task<string> GetRawAsync(string path, string? logPath = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
        };
        req.Headers.Pragma.ParseAdd("no-cache");
        req.Headers.Add("X-FluxoPrime-Desktop", "1");
        if (!string.IsNullOrWhiteSpace(_csrf)) req.Headers.Add("X-CSRF-Token", _csrf);
        var res = await _http.SendAsync(req);
        var json = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(json, $"Erro HTTP {(int)res.StatusCode}."));
        AppLog.Info($"GET {logPath ?? path}: {(int)res.StatusCode}, {json.Length} bytes");
        return json;
    }

    private bool TryReadCachedJson(string path, out string json)
    {
        json = "";
        try
        {
            var file = CacheFile(path);
            if (!File.Exists(file)) return false;
            json = File.ReadAllText(file);
            return !string.IsNullOrWhiteSpace(json);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"Read api cache {path}");
            return false;
        }
    }

    private void WriteCachedJson(string path, string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]") return;
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(CacheFile(path), json);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"Write api cache {path}");
        }
    }

    private string CacheFile(string path)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BaseUrl + path)));
        return Path.Combine(_cacheDir, $"{hash}.json");
    }

    private string MakeAbsoluteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)) return absolute.ToString();
        if (url.StartsWith('/')) return BaseUrl + url;
        return BaseUrl + "/" + url.TrimStart('/');
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static string ReadError(string json, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error)) return error.GetString() ?? fallback;
        }
        catch { }
        return fallback;
    }

    private static string? FirstString(JsonElement first, JsonElement second, params string[] names)
    {
        foreach (var name in names)
        {
            var value = JsonString(first, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
            value = JsonString(second, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static void ParseSeriesSeasons(JsonElement seasonsElement, SeriesInfo series)
    {
        if (seasonsElement.ValueKind != JsonValueKind.Array) return;
        foreach (var seasonElement in seasonsElement.EnumerateArray())
        {
            if (seasonElement.ValueKind != JsonValueKind.Object) continue;
            var number = JsonInt(seasonElement, "season_number", "season", "number", "id");
            if (number <= 0) number = series.Seasons.Count + 1;
            var season = EnsureSeason(series, number);
            season.Name = FirstString(seasonElement, seasonElement, "name", "title") ?? season.Name;
        }
    }

    private static void ParseSeriesEpisodes(JsonElement episodesElement, SeriesInfo series, StreamItem item)
    {
        if (episodesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var seasonProperty in episodesElement.EnumerateObject())
            {
                var seasonNumber = int.TryParse(seasonProperty.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeason)
                    ? parsedSeason
                    : 0;
                ParseEpisodeArray(seasonProperty.Value, series, item, seasonNumber);
            }
            return;
        }

        ParseEpisodeArray(episodesElement, series, item, 0);
    }

    private static void ParseEpisodeArray(JsonElement episodeArray, SeriesInfo series, StreamItem item, int fallbackSeason)
    {
        if (episodeArray.ValueKind != JsonValueKind.Array) return;

        foreach (var episodeElement in episodeArray.EnumerateArray())
        {
            if (episodeElement.ValueKind != JsonValueKind.Object) continue;
            var episodeInfo = episodeElement.TryGetProperty("info", out var infoElement) ? infoElement : episodeElement;
            var seasonNumber = JsonInt(episodeElement, "season", "season_number");
            if (seasonNumber <= 0) seasonNumber = fallbackSeason > 0 ? fallbackSeason : 1;
            var episodeNumber = JsonInt(episodeElement, "episode_num", "episode", "episode_number", "number");
            var episode = new SeriesEpisode
            {
                Id = JsonInt(episodeElement, "id", "episode_id", "stream_id"),
                Season = seasonNumber,
                Episode = episodeNumber,
                Title = FirstString(episodeElement, episodeInfo, "title", "name", "o_name") ?? "",
                Plot = FirstString(episodeElement, episodeInfo, "plot", "description", "desc") ?? "",
                Duration = FirstString(episodeElement, episodeInfo, "duration", "duration_secs", "duration_seconds", "runtime") ?? "",
                Icon = FirstString(episodeElement, episodeInfo, "movie_image", "cover", "image", "icon", "stream_icon") ?? item.Icon,
            };

            if (episode.Id <= 0) continue;
            EnsureSeason(series, seasonNumber).Episodes.Add(episode);
        }
    }

    private static SeriesSeason EnsureSeason(SeriesInfo series, int seasonNumber)
    {
        var season = series.Seasons.FirstOrDefault(existing => existing.Number == seasonNumber);
        if (season is not null) return season;
        season = new SeriesSeason { Number = seasonNumber, Name = $"Temporada {seasonNumber}" };
        series.Seasons.Add(season);
        return season;
    }

    private static int JsonInt(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        return 0;
    }

    private static string? JsonString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }
}
