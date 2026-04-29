using Foundation;
using WebKit;

namespace HelloWorld_IOS.Platforms.iOS;

// Serves static viewer assets (viewer.html + vendor/**) for the
// `nwdviewer-app://` scheme. Files come from the app bundle, materialised by
// MauiAsset. The host of the URL is ignored — only the path matters.
//
// Path layout:
//   nwdviewer-app:///viewer.html             -> Resources/Raw/wwwroot/viewer.html
//   nwdviewer-app:///vendor/three/build/...  -> Resources/Raw/wwwroot/vendor/three/build/...
public sealed class AppSchemeHandler : NSObject, IWKUrlSchemeHandler
{
    public const string Scheme = "nwdviewer-app";
    public const string EntryUrl = "nwdviewer-app:///viewer.html";

    [Export("webView:startURLSchemeTask:")]
    public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
    {
        var url = urlSchemeTask.Request.Url;
        try
        {
            var rawPath = url.Path ?? string.Empty;
            if (rawPath.StartsWith("/", StringComparison.Ordinal))
                rawPath = rawPath.Substring(1);
            var relPath = Uri.UnescapeDataString(rawPath);
            if (string.IsNullOrEmpty(relPath))
                relPath = "viewer.html";

            // Assets live under <app>/wwwroot/ in the bundle (see csproj's
            // MauiAsset Include="Resources\Raw\**" with the default LogicalName
            // mapping). The URL space is rooted at viewer.html with no
            // wwwroot/ prefix, so the handler prepends it here.
            var bundleRelPath = "wwwroot/" + relPath;
            using var stream = FileSystem.OpenAppPackageFileAsync(bundleRelPath).GetAwaiter().GetResult();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            var data = NSData.FromArray(bytes);

            var headers = new NSMutableDictionary
            {
                [(NSString)"Content-Type"] = (NSString)GuessMime(relPath),
                [(NSString)"Content-Length"] = (NSString)bytes.LongLength.ToString(),
                [(NSString)"Access-Control-Allow-Origin"] = (NSString)"*",
            };
            using var response = new NSHttpUrlResponse(url, 200, "HTTP/1.1", headers);
            urlSchemeTask.DidReceiveResponse(response);
            urlSchemeTask.DidReceiveData(data);
            urlSchemeTask.DidFinish();
        }
        catch (FileNotFoundException)
        {
            Fail(urlSchemeTask, 404);
        }
        catch (Exception ex)
        {
            try { urlSchemeTask.DidFailWithError(new NSError((NSString)"AppSchemeHandler", -1, NSDictionary.FromObjectAndKey((NSString)ex.Message, NSError.LocalizedDescriptionKey))); }
            catch { }
        }
    }

    [Export("webView:stopURLSchemeTask:")]
    public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask) { }

    private static void Fail(IWKUrlSchemeTask task, int status)
    {
        var headers = new NSMutableDictionary { [(NSString)"Content-Type"] = (NSString)"text/plain" };
        using var resp = new NSHttpUrlResponse(task.Request.Url, status, "HTTP/1.1", headers);
        task.DidReceiveResponse(resp);
        task.DidReceiveData(NSData.FromString(""));
        task.DidFinish();
    }

    private static string GuessMime(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js"   => "application/javascript; charset=utf-8",
            ".mjs"  => "application/javascript; charset=utf-8",
            ".css"  => "text/css; charset=utf-8",
            ".json" => "application/json",
            ".wasm" => "application/wasm",
            ".svg"  => "image/svg+xml",
            ".png"  => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
    }
}
