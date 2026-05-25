using System.Text.Json;

namespace FluxoPrimeCore;

public sealed class AdminApiClient
{
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    public string PanelUrl { get; set; } = "https://painel.fluxoprime.com";

    public AdminApiClient(string? panelUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(panelUrl))
            PanelUrl = panelUrl.TrimEnd('/');
    }

    public async Task<string?> GetUpstreamUrlAsync()
    {
        var status = await GetStatusAsync();
        return status.PrimaryUrl;
    }

    public async Task<AdminPanelStatus> GetStatusAsync()
    {
        try
        {
            var url = $"{PanelUrl}/api.php?action=config";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var primaryUrl = root.TryGetProperty("upstreamUrl", out var upstreamUrlElement)
                ? upstreamUrlElement.GetString() ?? ""
                : "";
            var primaryName = "";
            var activeCount = 0;

            if (root.TryGetProperty("upstreams", out var upstreamsElement)
                && upstreamsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var upstreamElement in upstreamsElement.EnumerateArray())
                {
                    if (upstreamElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!IsEnabled(upstreamElement))
                    {
                        continue;
                    }

                    activeCount++;
                    var upstream = upstreamElement.TryGetProperty("url", out var urlElement)
                        ? urlElement.GetString() ?? ""
                        : "";
                    if (string.IsNullOrWhiteSpace(primaryUrl) && !string.IsNullOrWhiteSpace(upstream))
                    {
                        primaryUrl = upstream;
                    }

                    if (string.IsNullOrWhiteSpace(primaryName)
                        && upstreamElement.TryGetProperty("name", out var nameElement))
                    {
                        primaryName = nameElement.GetString() ?? "";
                    }
                }
            }

            if (activeCount == 0 && !string.IsNullOrWhiteSpace(primaryUrl))
            {
                activeCount = 1;
            }

            AppLog.Info($"AdminPanel: active={activeCount}, primary={(string.IsNullOrWhiteSpace(primaryUrl) ? "none" : "set")}");
            return new AdminPanelStatus(true, PanelUrl, primaryUrl, primaryName, activeCount, "");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "AdminPanel GetStatus");
            return new AdminPanelStatus(false, PanelUrl, "", "", 0, ex.Message);
        }
    }

    private static bool IsEnabled(JsonElement upstreamElement)
    {
        if (!upstreamElement.TryGetProperty("enabled", out var enabledElement))
        {
            return true;
        }

        return enabledElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => !enabledElement.TryGetInt32(out var value) || value != 0,
            JsonValueKind.String => IsEnabledString(enabledElement.GetString()),
            _ => true,
        };
    }

    private static bool IsEnabledString(string? value)
    {
        var normalized = (value ?? "").Trim();
        return !string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record AdminPanelStatus(
    bool IsAvailable,
    string PanelUrl,
    string PrimaryUrl,
    string PrimaryName,
    int ActiveCount,
    string ErrorMessage);
