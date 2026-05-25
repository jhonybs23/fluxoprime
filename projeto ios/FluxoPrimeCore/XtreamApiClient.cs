using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace FluxoPrimeCore;

public sealed class StringOrNumberConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => reader.GetString()
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

public sealed class XtreamApiClient
{
    private readonly HttpClient _http = new();
    private string _baseUrl = "";
    private string _username = "";
    private string _password = "";

    public string BaseUrl => _baseUrl;
    public string Username => _username;
    public string Password => _password;
    public bool LastRequestFromCache { get; private set; }

    public void Configure(string baseUrl, string username, string password)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _username = username;
        _password = password;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_baseUrl)
        && !string.IsNullOrWhiteSpace(_username)
        && !string.IsNullOrWhiteSpace(_password);

    public async Task<UserInfo> AuthenticateAsync()
    {
        if (!IsConfigured) throw new InvalidOperationException("API nao configurada. Defina DNS, usuario e senha.");
        var json = await GetAsync("");
        var doc = JsonDocument.Parse(json);
        var userInfo = doc.RootElement.GetProperty("user_info");
        return new UserInfo
        {
            Username = JsonString(userInfo, "username") ?? _username,
            Status = JsonString(userInfo, "status") ?? "Unknown",
            ExpDate = JsonString(userInfo, "exp_date") ?? "",
            IsTrial = JsonString(userInfo, "is_trial") ?? "0",
            MaxConnections = JsonString(userInfo, "max_connections") ?? "1",
            ActiveCons = JsonString(userInfo, "active_cons") ?? "0",
            CreatedAt = JsonString(userInfo, "created_at") ?? "",
        };
    }

    public async Task<List<CategoryItem>> GetCategoriesAsync(string section)
    {
        var action = section switch
        {
            "vod" => "get_vod_categories",
            "series" => "get_series_categories",
            _ => "get_live_categories",
        };
        var json = await GetAsync($"&action={action}");
        return JsonSerializer.Deserialize<List<CategoryItem>>(json, JsonOptions()) ?? [];
    }

    public async Task<List<StreamItem>> GetStreamsAsync(string section)
    {
        var action = section switch
        {
            "vod" => "get_vod_streams",
            "series" => "get_series",
            _ => "get_live_streams",
        };
        var json = await GetAsync($"&action={action}");
        var items = JsonSerializer.Deserialize<List<XtreamStream>>(json, JsonOptions()) ?? [];
        return items.Select(MapStream).ToList();
    }

    public async Task<VodInfo?> GetVodInfoAsync(int streamId)
    {
        var json = await GetAsync($"&action=get_vod_info&vod_id={streamId}");
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("info", out var infoEl)) return null;
        var movieData = infoEl.TryGetProperty("movie_data", out var md) ? md : infoEl;
        return new VodInfo
        {
            Title = JsonString(movieData, "name") ?? "",
            Plot = JsonString(movieData, "plot") ?? "",
            Genre = JsonString(movieData, "genre") ?? "",
            Cast = JsonString(movieData, "cast") ?? "",
            Director = JsonString(movieData, "director") ?? "",
            Rating = JsonString(movieData, "rating") ?? "",
            Duration = JsonString(movieData, "duration") ?? JsonString(movieData, "duration_secs") ?? "",
            ReleaseDate = JsonString(movieData, "releasedate") ?? JsonString(movieData, "releaseDate") ?? JsonString(movieData, "year") ?? "",
            Cover = JsonString(movieData, "cover") ?? JsonString(movieData, "movie_image") ?? "",
            StreamId = JsonInt(movieData, "stream_id"),
        };
    }

    public async Task<SeriesInfoXtream?> GetSeriesInfoAsync(int seriesId)
    {
        var json = await GetAsync($"&action=get_series_info&series_id={seriesId}");
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("info", out var infoEl)) return null;
        var series = new SeriesInfoXtream
        {
            Title = JsonString(infoEl, "name") ?? "",
            Plot = JsonString(infoEl, "plot") ?? "",
            Genre = JsonString(infoEl, "genre") ?? "",
            Cast = JsonString(infoEl, "cast") ?? "",
            Director = JsonString(infoEl, "director") ?? "",
            Rating = JsonString(infoEl, "rating") ?? "",
            ReleaseDate = JsonString(infoEl, "releasedate") ?? JsonString(infoEl, "year") ?? "",
            Cover = JsonString(infoEl, "cover") ?? JsonString(infoEl, "movie_image") ?? "",
        };

        if (doc.RootElement.TryGetProperty("episodes", out var episodesEl) && episodesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var seasonProp in episodesEl.EnumerateObject())
            {
                var seasonNumber = int.TryParse(seasonProp.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sn) ? sn : 0;
                var season = new SeriesSeasonXtream
                {
                    Number = seasonNumber,
                    Name = $"Temporada {seasonNumber}",
                };
                foreach (var ep in seasonProp.Value.EnumerateArray())
                {
                    var epInfo = ep.TryGetProperty("info", out var ei) ? ei : ep;
                    season.Episodes.Add(new SeriesEpisodeXtream
                    {
                        Id = JsonInt(ep, "id"),
                        Season = seasonNumber,
                        Episode = JsonInt(ep, "episode_num"),
                        Title = JsonString(epInfo, "title") ?? JsonString(ep, "title") ?? "",
                        Plot = JsonString(epInfo, "plot") ?? "",
                        Duration = JsonString(epInfo, "duration") ?? "",
                        Icon = JsonString(epInfo, "movie_image") ?? JsonString(ep, "movie_image") ?? "",
                    });
                }
                series.Seasons.Add(season);
            }
        }
        return series;
    }

    public string GetPlayUrl(StreamItem item)
    {
        var ext = item.Type switch
        {
            "movie" or "vod" => "mp4",
            "series" => "mp4",
            _ => "ts",
        };
        var path = item.Type switch
        {
            "movie" or "vod" => "movie",
            "series" => "series",
            _ => "live",
        };
        return $"{_baseUrl}/{path}/{_username}/{_password}/{item.Id}.{ext}";
    }

    private async Task<string> GetAsync(string query)
    {
        if (!IsConfigured) throw new InvalidOperationException("API nao configurada.");
        var url = $"{_baseUrl}/player_api.php?username={Uri.EscapeDataString(_username)}&password={Uri.EscapeDataString(_password)}{query}";
        var json = await _http.GetStringAsync(url);
        AppLog.Info($"XTREAM GET: {url[(url.IndexOf("action=") + 7)..] ?? url[^50..]}");
        return json;
    }

    private static StreamItem MapStream(XtreamStream s)
    {
        return new StreamItem
        {
            Id = s.StreamId != 0 ? s.StreamId : s.SeriesId != 0 ? s.SeriesId : 0,
            Name = s.Name,
            Icon = !string.IsNullOrWhiteSpace(s.StreamIcon) ? s.StreamIcon : s.Cover,
            Category = s.CategoryId,
            Type = s.StreamType switch
            {
                "movie" => "movie",
                "series" => "series",
                _ => "live",
            },
            Rating = s.Rating ?? "",
            Year = s.Year ?? "",
            EpgNowTitle = s.EpgTitle ?? "",
            EpgNowTime = s.EpgTime ?? "",
        };
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static string? JsonString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }

    private static int JsonInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) return n;
        return 0;
    }

    private class XtreamStream
    {
        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }
        [JsonPropertyName("series_id")]
        public int SeriesId { get; set; }
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("stream_icon")]
        public string StreamIcon { get; set; } = "";
        [JsonPropertyName("cover")]
        public string Cover { get; set; } = "";
        [JsonPropertyName("category_id")]
        public int CategoryId { get; set; }
        [JsonPropertyName("stream_type")]
        public string StreamType { get; set; } = "";
        [JsonPropertyName("rating")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string Rating { get; set; } = "";
        [JsonPropertyName("year")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string Year { get; set; } = "";
        [JsonPropertyName("epg_title")]
        public string EpgTitle { get; set; } = "";
        [JsonPropertyName("epg_time")]
        public string EpgTime { get; set; } = "";
    }
}

public sealed class UserInfo
{
    public string Username { get; set; } = "";
    public string Status { get; set; } = "";
    public string ExpDate { get; set; } = "";
    public string IsTrial { get; set; } = "";
    public string MaxConnections { get; set; } = "";
    public string ActiveCons { get; set; } = "";
    public string CreatedAt { get; set; } = "";

    public string DisplayStatus => Status == "Active" ? "Ativo" : Status;
}

public sealed class VodInfo
{
    public string Title { get; set; } = "";
    public string Plot { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Cast { get; set; } = "";
    public string Director { get; set; } = "";
    public string Rating { get; set; } = "";
    public string Duration { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string Cover { get; set; } = "";
    public int StreamId { get; set; }
}

public sealed class SeriesInfoXtream
{
    public string Title { get; set; } = "";
    public string Plot { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Cast { get; set; } = "";
    public string Director { get; set; } = "";
    public string Rating { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string Cover { get; set; } = "";
    public List<SeriesSeasonXtream> Seasons { get; set; } = [];
}

public sealed class SeriesSeasonXtream
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public List<SeriesEpisodeXtream> Episodes { get; set; } = [];
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Temporada {Number}" : Name;
}

public sealed class SeriesEpisodeXtream
{
    public int Id { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
    public string Title { get; set; } = "";
    public string Plot { get; set; } = "";
    public string Duration { get; set; } = "";
    public string Icon { get; set; } = "";
    public string DisplayTitle => Episode > 0 && !string.IsNullOrWhiteSpace(Title)
        ? $"E{Episode:00} - {Title}"
        : $"E{Episode:00}";
}
