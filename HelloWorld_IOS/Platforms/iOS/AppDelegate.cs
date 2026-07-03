using Foundation;
using HelloWorld_IOS.Services;
using UIKit;

namespace HelloWorld_IOS;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
	{
		var result = base.FinishedLaunching(application, launchOptions);
		// iOS broadcasts a memory warning before jettisoning processes (including the
		// WKWebView content process that hosts the viewer). Logging it timestamps the
		// pressure build-up so session logs show warnings *preceding* each
		// "WebView content process terminated" line instead of kills appearing from nowhere.
		NSNotificationCenter.DefaultCenter.AddObserver(
			UIApplication.DidReceiveMemoryWarningNotification,
			_ => Logger.Warn("app.memory", "iOS memory warning — system is under memory pressure"));
		return result;
	}
}
