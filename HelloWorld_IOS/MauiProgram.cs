using HelloWorld_IOS.Controls;
using HelloWorld_IOS.Services;
using HelloWorld_IOS.ViewModels;
using HelloWorld_IOS.Views;
using Microsoft.Extensions.Logging;

namespace HelloWorld_IOS;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			})
			.ConfigureMauiHandlers(handlers =>
			{
#if IOS
				handlers.AddHandler<NwdWebView, HelloWorld_IOS.Platforms.iOS.NwdWebViewHandler>();
#endif
			});

		builder.Services.AddSingleton<MainViewModel>();
		builder.Services.AddSingleton<CredentialStore>();
		builder.Services.AddSingleton<TabFileStore>();
		builder.Services.AddTransient<PickedFileImporter>();
		builder.Services.AddTransient<ViewerPage>();
		builder.Services.AddTransient<SettingsViewModel>();
		builder.Services.AddTransient<SettingsPage>();

		// APS factory: per-call construction of the HttpClient + three APS
		// clients. Cheap (no I/O at construction). MainViewModel's TranslateAsync
		// owns the lifetime via `using`.
		builder.Services.AddSingleton<Func<ApsCredentials, ApsServices>>(_ => creds => new ApsServices(creds));

		builder.Services.AddHttpClient();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
