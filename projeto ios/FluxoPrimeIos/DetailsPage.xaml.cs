using FluxoPrimeMaui.ViewModels;

namespace FluxoPrimeMaui;

public partial class DetailsPage : ContentPage
{
    private readonly DetailsViewModel _vm;

    public DetailsPage(DetailsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.InitializeAsync();
    }
}
