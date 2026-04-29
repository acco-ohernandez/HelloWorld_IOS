using HelloWorld_IOS.Views;

namespace HelloWorld_IOS;

public partial class App : Application
{
	private readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Resolve ViewerPage via DI so its (MainViewModel, TabFileStore,
		// PickedFileImporter) constructor parameters are wired automatically.
		// We bypass AppShell because Shell's DataTemplate route requires a
		// parameterless constructor.
		var page = _services.GetRequiredService<ViewerPage>();
		return new Window(new NavigationPage(page) { BarBackgroundColor = Color.FromArgb("#2b2b2b") });
	}
}
