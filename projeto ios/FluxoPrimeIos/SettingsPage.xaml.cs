using FluxoPrimeMaui.ViewModels;

namespace FluxoPrimeMaui;

public partial class SettingsPage : ContentPage
{
    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
