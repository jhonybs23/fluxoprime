using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluxoPrimeCore;
using FluxoPrimeMaui.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;

namespace FluxoPrimeMaui.ViewModels;

[QueryProperty(nameof(Stream), "stream")]
public partial class PlayerViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly AppLibraryStore _libraryStore;
    private string _liveExtension = DefaultLiveExtension;

    public bool PrefersLiveHls => PlatformPrefersHls;

    [ObservableProperty]
    private StreamItem _stream = new();

    [ObservableProperty]
    private string _streamUrl = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private Aspect _videoAspect = Aspect.AspectFill;

    [ObservableProperty]
    private string _aspectButtonText = "Ajustar";

    public PlayerViewModel(ApiClient api, AppLibraryStore libraryStore)
    {
        _api = api;
        _libraryStore = libraryStore;
    }

    public async Task InitializeAsync()
    {
        if (Stream.Id == 0) return;

        IsLoading = true;
        ErrorMessage = "";
        HasError = false;
        _liveExtension = Stream.Type == "live" ? DefaultLiveExtension : "";
        try
        {
            _libraryStore.AddRecent(Stream);
            await LoadPlaybackUrlAsync(refresh: false, liveExtension: null);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Initialize player");
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<string> RenewLiveUrlAsync(bool preferHls)
    {
        if (Stream.Type != "live")
        {
            return StreamUrl;
        }

        IsLoading = true;
        ErrorMessage = "";
        HasError = false;
        try
        {
            var liveExtension = preferHls || PlatformPrefersHls ? "m3u8" : "ts";
            await LoadPlaybackUrlAsync(refresh: true, liveExtension);
            AppLog.Warn($"Live URL renovada: {Stream.Id} ({_liveExtension})");
            return StreamUrl;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Renew live URL");
            ErrorMessage = "A live oscilou e nao consegui renovar a conexao.";
            HasError = true;
            return "";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void SetRecovering(bool isRecovering)
    {
        IsLoading = isRecovering;
        if (isRecovering)
        {
            HasError = false;
            ErrorMessage = "";
        }
    }

    public void SetPlaybackError(string message)
    {
        ErrorMessage = message;
        HasError = true;
        IsLoading = false;
    }

    [RelayCommand]
    private void ToggleAspect()
    {
        if (VideoAspect == Aspect.AspectFill)
        {
            VideoAspect = Aspect.AspectFit;
            AspectButtonText = "Preencher";
            return;
        }

        VideoAspect = Aspect.AspectFill;
        AspectButtonText = "Ajustar";
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        await Shell.Current.GoToAsync("..");
    }

    private async Task LoadPlaybackUrlAsync(bool refresh, string? liveExtension)
    {
        var extension = Stream.Type == "live"
            ? !string.IsNullOrWhiteSpace(liveExtension) ? liveExtension : _liveExtension
            : null;

        var play = await _api.PlayUrlAsync(Stream, extension, refresh);
        StreamUrl = play.Url;
        if (Stream.Type == "live")
        {
            _liveExtension = string.IsNullOrWhiteSpace(extension) ? DefaultLiveExtension : extension;
        }

        if (string.IsNullOrWhiteSpace(StreamUrl))
        {
            ErrorMessage = "A API nao retornou uma URL de reproducao.";
            HasError = true;
        }
    }

    private static bool PlatformPrefersHls =>
        DeviceInfo.Platform == DevicePlatform.iOS;

    private static string DefaultLiveExtension => PlatformPrefersHls ? "m3u8" : "ts";
}
