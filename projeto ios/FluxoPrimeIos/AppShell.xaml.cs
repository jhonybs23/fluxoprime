namespace FluxoPrimeMaui;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		Routing.RegisterRoute("player", typeof(PlayerPage));
		Routing.RegisterRoute("details", typeof(DetailsPage));
	}
}