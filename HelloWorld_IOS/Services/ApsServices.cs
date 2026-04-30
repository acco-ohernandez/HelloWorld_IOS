using NwdViewer.Aps;

namespace HelloWorld_IOS.Services;

// Direct port of NwdViewer.Desktop/Services/ApsServices.cs.
// Facade over the three APS clients with a shared HttpClient (10-min timeout
// for big NWD uploads). Cheap to construct; created per-translation by
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
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        Auth = new AuthClient(Http, Options);
        Oss = new OssClient(Http, Auth, Options);
        ModelDerivative = new ModelDerivativeClient(Http, Auth, Options);
    }

    public void Dispose() => Http.Dispose();
}
