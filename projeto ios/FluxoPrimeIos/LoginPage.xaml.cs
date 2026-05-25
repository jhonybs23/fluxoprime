using FluxoPrimeMaui.ViewModels;

namespace FluxoPrimeMaui;

public partial class LoginPage : ContentPage
{
	public LoginPage(LoginViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
	}
}
