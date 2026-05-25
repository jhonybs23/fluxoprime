using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxoPrimeCore;

namespace FluxoPrimeMaui.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISessionService _sessionService;

    [ObservableProperty]
    private string _baseUrl = "";

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isLoading;

    public SettingsViewModel(ISessionService sessionService)
    {
        _sessionService = sessionService;
        _ = LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        var (user, pass, url) = await _sessionService.LoadSessionAsync();
        BaseUrl = url;
        Username = user;
        Password = pass;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsLoading = true;
        StatusMessage = "";
        try
        {
            await _sessionService.SaveSessionAsync(Username, Password, BaseUrl.Trim().TrimEnd('/'));
            StatusMessage = "Configuracoes salvas com sucesso.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        await _sessionService.ClearSessionAsync();
        BaseUrl = "";
        Username = "";
        Password = "";
        StatusMessage = "Dados apagados.";
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        await Shell.Current.GoToAsync("..");
    }
}
