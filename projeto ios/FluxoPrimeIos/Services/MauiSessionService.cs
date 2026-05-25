using FluxoPrimeCore;

namespace FluxoPrimeMaui.Services;

public class MauiSessionService : ISessionService
{
    private const string UserKey = "fluxo_username";
    private const string PassKey = "fluxo_password";
    private const string UrlKey = "fluxo_baseurl";

    public async Task SaveSessionAsync(string username, string password, string baseUrl)
    {
        await SecureStorage.SetAsync(UserKey, username);
        await SecureStorage.SetAsync(PassKey, password);
        await SecureStorage.SetAsync(UrlKey, baseUrl);
    }

    public async Task<(string Username, string Password, string BaseUrl)> LoadSessionAsync()
    {
        var user = await SecureStorage.GetAsync(UserKey) ?? "";
        var pass = await SecureStorage.GetAsync(PassKey) ?? "";
        var url = await SecureStorage.GetAsync(UrlKey) ?? "";
        return (user, pass, url);
    }

    public Task ClearSessionAsync()
    {
        SecureStorage.Remove(UserKey);
        SecureStorage.Remove(PassKey);
        SecureStorage.Remove(UrlKey);
        return Task.CompletedTask;
    }

    public async Task<bool> HasSessionAsync()
    {
        return !string.IsNullOrEmpty(await SecureStorage.GetAsync(UserKey));
    }
}
