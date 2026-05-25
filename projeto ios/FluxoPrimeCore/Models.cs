using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxoPrimeCore;

public sealed class UserMe
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = "";
}

public sealed class CategoryItem
{
    [JsonPropertyName("category_id")]
    public int Id { get; set; }

    [JsonPropertyName("category_name")]
    public string Name { get; set; } = "";
}

public sealed class StreamItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("series_id")]
    public int SeriesId
    {
        get => Id;
        set { if (Id == 0) Id = value; }
    }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title
    {
        get => Name;
        set { if (string.IsNullOrWhiteSpace(Name)) Name = value; }
    }

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("stream_icon")]
    public string StreamIcon
    {
        get => Icon;
        set { if (string.IsNullOrWhiteSpace(Icon)) Icon = value; }
    }

    [JsonPropertyName("cover")]
    public string Cover
    {
        get => Icon;
        set { if (string.IsNullOrWhiteSpace(Icon)) Icon = value; }
    }

    [JsonPropertyName("category")]
    public int Category { get; set; }

    [JsonPropertyName("category_id")]
    public int CategoryId
    {
        get => Category;
        set { if (Category == 0) Category = value; }
    }

    public string Type { get; set; } = "live";

    public int DisplayIndex { get; set; }

    public bool IsEpisode { get; set; }

    [JsonPropertyName("rating")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Rating { get; set; } = "";

    [JsonPropertyName("year")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Year { get; set; } = "";

    public string EpgNowTitle { get; set; } = "";
    public string EpgNowTime { get; set; } = "";
    public string EpgNextTitle { get; set; } = "";
    public string EpgNowSubtitle => string.IsNullOrWhiteSpace(EpgNowTitle)
        ? "ao vivo"
        : $"{EpgNowTime}  {EpgNowTitle}";
}

public sealed class PlayResponse
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("direct")]
    public bool Direct { get; set; }

    [JsonPropertyName("extension")]
    public string Extension { get; set; } = "";

    [JsonPropertyName("mime")]
    public string Mime { get; set; } = "";

    [JsonPropertyName("candidates")]
    public List<PlayCandidate> Candidates { get; set; } = [];
}

public sealed class PlayCandidate
{
    [JsonPropertyName("extension")]
    public string Extension { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("mime")]
    public string Mime { get; set; } = "";
}

public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "",
            JsonTokenType.Number => reader.GetDouble().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => "",
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

public sealed class MovieInfo
{
    public string Title { get; set; } = "";
    public string Plot { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Cast { get; set; } = "";
    public string Director { get; set; } = "";
    public string Rating { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string Duration { get; set; } = "";
    public string ImdbId { get; set; } = "";
    public string TmdbId { get; set; } = "";
}

public sealed class SeriesInfo
{
    public string Title { get; set; } = "";
    public string Plot { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Cast { get; set; } = "";
    public string Director { get; set; } = "";
    public string Rating { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string Cover { get; set; } = "";
    public List<SeriesSeason> Seasons { get; set; } = [];
}

public sealed class SeriesSeason
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public List<SeriesEpisode> Episodes { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"Temporada {Number}"
        : Name;
}

public sealed class SeriesEpisode
{
    public int Id { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
    public string Title { get; set; } = "";
    public string Plot { get; set; } = "";
    public string Duration { get; set; } = "";
    public string Icon { get; set; } = "";
    public bool IsWatched { get; set; }
    public bool IsCurrent { get; set; }

    public string DisplayTitle
    {
        get
        {
            var code = Season > 0 && Episode > 0 ? $"S{Season:00}E{Episode:00}" : Episode > 0 ? $"E{Episode:00}" : "Episodio";
            return string.IsNullOrWhiteSpace(Title) ? code : $"{code} - {Title}";
        }
    }

    public string EpisodeCode => Season > 0 && Episode > 0
        ? $"S{Season:00}E{Episode:00}"
        : Episode > 0 ? $"E{Episode:00}" : "EP";

    public string CardTitle
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title)) return EpisodeCode;
            var title = Title
                .Replace(EpisodeCode, "", StringComparison.OrdinalIgnoreCase)
                .Replace(EpisodeCode.Replace("E", " E"), "", StringComparison.OrdinalIgnoreCase)
                .Trim(' ', '-', '|');
            return string.IsNullOrWhiteSpace(title) ? EpisodeCode : title;
        }
    }

    public string WatchStatus => IsCurrent ? "Continuar" : IsWatched ? "Visto" : "Novo";

    public string DisplaySubtitle => string.IsNullOrWhiteSpace(Duration) || Duration == "0" ? "episodio" : Duration;
}
