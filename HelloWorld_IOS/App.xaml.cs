using HelloWorld_IOS.Services;
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

	// Raised when the app returns to the foreground. ViewerPage subscribes to ask the
	// viewer to verify its WebGL context survived the background (iOS reclaims the GPU).
	public static event Action? Resumed;

	// MAUI app-lifecycle hooks. Useful for debugging "did the app go to background
	// while a translation was running?" - the gap in timestamps between OnSleep
	// and OnResume corresponds to time spent suspended by iOS.
	protected override void OnStart()  => Logger.Info("app.lifecycle", "OnStart (foreground)");
	protected override void OnSleep()  => Logger.Info("app.lifecycle", "OnSleep (backgrounded)");
	protected override void OnResume()
	{
		Logger.Info("app.lifecycle", "OnResume (returned to foreground)");
		Resumed?.Invoke();
	}
}
