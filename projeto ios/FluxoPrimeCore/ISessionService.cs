namespace FluxoPrimeCore;

public interface ISessionService
{
    Task SaveSessionAsync(string username, string password, string baseUrl);
    Task<(string Username, string Password, string BaseUrl)> LoadSessionAsync();
    Task ClearSessionAsync();
    Task<bool> HasSessionAsync();
}
