using NwdViewer.Aps;

namespace HelloWorld_IOS.Services;

// Direct port of NwdViewer.Desktop/Services/ApsServices.cs.
// Facade over the three APS clients sharing one HttpClient. The client runs with an
// infinite timeout: a single global cap can't suit both a multi-minute large-file upload
// and a sub-second token call (a flat 10-min cap is exactly what killed a 476 MB upload on
// 5G at 600 s). Each logical call applies its own timeout via ApsHttp (size-aware for the
// upload, ~100 s for short calls). Cheap to construct; created per-translation by
// MainViewModel and disposed via `using`.
public sealed class ApsServices : IDisposable
{
    public HttpClient Http { get; }
    public ApsOptions Options { get; }
    public AuthClient Auth { get; }
    public OssClient Oss { get; }
    public ModelDerivativeClient ModelDerivative { get; }

    public ApsServices(ApsCredentials creds)
    {
        Options = new ApsOptions
        {
            ClientId = creds.ClientId,
            ClientSecret = creds.ClientSecret,
            BucketKey = creds.BucketKey,
        };
        Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Auth = new AuthClient(Http, Options);
        Oss = new OssClient(Http, Auth, Options);
        ModelDerivative = new ModelDerivativeClient(Http, Auth, Options);
    }

    public void Dispose() => Http.Dispose();
}
