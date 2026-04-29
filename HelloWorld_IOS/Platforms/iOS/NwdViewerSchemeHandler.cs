using Foundation;
using HelloWorld_IOS.Services;
using WebKit;

namespace HelloWorld_IOS.Platforms.iOS;

// WKURLSchemeHandler that serves files from per-tab cache folders for the
// custom `nwdviewer-files://{tabId}/{filename}` scheme. Mirrors the WPF
// OnWebResourceRequested handler at MainWindow.xaml.cs:429-479, including the
// path-traversal guard.
public sealed class NwdViewerSchemeHandler : NSObject, IWKUrlSchemeHandler
{
    public const string Scheme = "nwdviewer-files";

    private readonly TabFileStore _store;

    public NwdViewerSchemeHandler(TabFileStore store)
    {
        _store = store;
    }

    [Export("webView:startURLSchemeTask:")]
    public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
    {
        var url = urlSchemeTask.Request.Url;
        try
        {
            // URL shape: nwdviewer-files://{tabId}/{filename}
            // Host = tabId, Path = "/{filename}"
            if (!int.TryParse(url.Host, out var tabId))
            {
                Fail(urlSchemeTask, 400);
                return;
            }

            var rawPath = url.Path ?? string.Empty;
            if (rawPath.StartsWith("/", StringComparison.Ordinal))
                rawPath = rawPath.Substring(1);
            var filename = Uri.UnescapeDataString(rawPath);

            if (!_store.TryResolve(tabId, filename, out var fullPath))
            {
                Fail(urlSchemeTask, 404);
                return;
            }

            var data = NSData.FromFile(fullPath);
            if (data is null)
            {
                Fail(urlSchemeTask, 500);
                return;
            }

            var headers = new NSMutableDictionary
            {
                [(NSString)"Content-Type"] = (NSString)GuessMime(filename),
                [(NSString)"Content-Length"] = (NSString)data.Length.ToString(),
                [(NSString)"Access-Control-Allow-Origin"] = (NSString)"*",
                [(NSString)"Cache-Control"] = (NSString)"no-store",
            };

            using var response = new NSHttpUrlResponse(url, 200, "HTTP/1.1", headers);
            urlSchemeTask.DidReceiveResponse(response);
            urlSchemeTask.DidReceiveData(data);
            urlSchemeTask.DidFinish();
        }
        catch (Exception ex)
        {
            try { urlSchemeTask.DidFailWithError(new NSError((NSString)"NwdViewerSchemeHandler", -1, NSDictionary.FromObjectAndKey((NSString)ex.Message, NSError.LocalizedDescriptionKey))); }
            catch { /* WebKit may have already cancelled the task */ }
        }
    }

    [Export("webView:stopURLSchemeTask:")]
    public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
    {
        // WebKit cancelled the request (navigation, dispose). Nothing to clean
        // up — we serve synchronously from disk.
    }

    private static void Fail(IWKUrlSchemeTask task, int status)
    {
        var headers = new NSMutableDictionary { [(NSString)"Content-Type"] = (NSString)"text/plain" };
        using var resp = new NSHttpUrlResponse(task.Request.Url, status, "HTTP/1.1", headers);
        task.DidReceiveResponse(resp);
        task.DidReceiveData(NSData.FromString(""));
        task.DidFinish();
    }

    private static string GuessMime(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext switch
        {
            ".gltf" => "model/gltf+json",
            ".glb"  => "model/gltf-binary",
            ".obj"  => "text/plain",
            ".mtl"  => "text/plain",
            ".fbx"  => "application/octet-stream",
            ".stl"  => "application/octet-stream",
            ".ifc"  => "application/x-step",
            ".bin"  => "application/octet-stream",
            ".png"  => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream",
        };
    }
}
