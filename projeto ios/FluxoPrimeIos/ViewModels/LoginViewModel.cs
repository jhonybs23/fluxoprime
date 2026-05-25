using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxoPrimeCore;

namespace FluxoPrimeMaui.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly AdminApiClient _adminApi;

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _dnsStatusMessage = "Sincronizando DNS...";

    [ObservableProperty]
    private bool _isDnsReady;

    public LoginViewModel(ApiClient api, AdminApiClient adminApi)
    {
        _api = api;
        _adminApi = adminApi;
        _ = RefreshDnsAsync();
    }

    [RelayCommand]
    private async Task RefreshDnsAsync()
    {
        DnsStatusMessage = "Sincronizando DNS...";
        IsDnsReady = false;

        var status = await _adminApi.GetStatusAsync();
        if (status.IsAvailable && status.ActiveCount > 0)
        {
            DnsStatusMessage = "DNS sincronizado pelo painel";
            IsDnsReady = true;
            return;
        }

        DnsStatusMessage = string.IsNullOrWhiteSpace(status.ErrorMessage)
            ? "Painel DNS sem servidor ativo."
            : $"Painel DNS indisponivel: {status.ErrorMessage}";
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Preencha usuario e senha";
            return;
        }

        IsLoading = true;
        ErrorMessage = "";

        try
        {
            await RefreshDnsAsync();
            if (!IsDnsReady)
            {
                ErrorMessage = "Nao encontrei DNS ativo no painel. Verifique o painel FluxoPrime.";
                return;
            }

            await _api.LoginAsync(Username.Trim(), Password);
            await Shell.Current.GoToAsync("//main");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
