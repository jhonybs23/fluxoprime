using CommunityToolkit.Maui;
using FluxoPrimeCore;
using FluxoPrimeMaui.Converters;
using FluxoPrimeMaui.Services;
using FluxoPrimeMaui.ViewModels;

namespace FluxoPrimeMaui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.UseMauiCommunityToolkitMediaElement()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// Serviços Core
		builder.Services.AddSingleton<ISessionService, MauiSessionService>();
		builder.Services.AddSingleton<AppLibraryStore>();
		builder.Services.AddSingleton<AdminApiClient>();
		builder.Services.AddSingleton(provider => new ApiClient(provider.GetRequiredService<ISessionService>()));

		// ViewModels
		builder.Services.AddTransient<LoginViewModel>();
		builder.Services.AddTransient<MainViewModel>();
		builder.Services.AddTransient<PlayerViewModel>();
		builder.Services.AddTransient<DetailsViewModel>();

		// Pages
		builder.Services.AddTransient<LoginPage>();
		builder.Services.AddTransient<MainPage>();
		builder.Services.AddTransient<PlayerPage>();
		builder.Services.AddTransient<DetailsPage>();

		return builder.Build();
	}
}
