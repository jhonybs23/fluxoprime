using FluxoPrimeCore;

namespace FluxoPrimeMaui;

public partial class App : Application
{
	private readonly ApiClient _api;

	public App(ApiClient api)
	{
		InitializeComponent();
		_api = api;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}

	protected override async void OnStart()
	{
		base.OnStart();
		try
		{
			if (await _api.TryAutoLoginAsync())
			{
				await Shell.Current.GoToAsync("//main");
			}
		}
		catch (Exception ex)
		{
			AppLog.Error(ex, "AutoLogin");
		}
	}
}
