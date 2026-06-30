using System.Diagnostics;
using System.Net.Http.Json;

namespace NwdViewer.Aps;

public sealed class AuthClient
{
    private readonly HttpClient _http;
    private readonly ApsOptions _options;
    private readonly Dictionary<string, (TokenResponse Token, DateTimeOffset ExpiresAt)> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AuthClient(HttpClient http, ApsOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<string> GetInternalTokenAsync(CancellationToken ct = default)
        => await GetTokenAsync("data:read data:write data:create bucket:create bucket:read", ct);

    public async Task<string> GetViewerTokenAsync(CancellationToken ct = default)
        => await GetTokenAsync("viewables:read", ct);

    // Cache is keyed by scope string. The previous implementation cached a single token
    // regardless of scope; once a viewer-scoped token was fetched, every subsequent
    // internal-scoped request returned it too, which APS rejects on Model Derivative
    // with "Token exchange access denied".
    private async Task<string> GetTokenAsync(string scope, CancellationToken ct)
    {
        _options.Validate();
        await _lock.WaitAsync(ct);
        try
        {
            var scopeLabel = ScopeLabel(scope);
            if (_cache.TryGetValue(scope, out var entry) && DateTimeOffset.UtcNow < entry.ExpiresAt)
            {
                var ttl = (int)(entry.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
                ApsLog.Info("aps.auth", $"cache hit scope={scopeLabel} ttl={ttl}s");
                return entry.Token.AccessToken;
            }

            ApsLog.Info("aps.auth", $"cache miss scope={scopeLabel} - fetching token");

            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", scope),
            });

            var auth = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/authentication/v2/token")
            {
                Content = body,
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);

            var sw = Stopwatch.StartNew();
            using var resp = await ApsHttp.SendAsync(_http, req, ApsHttp.ShortCallTimeout, "APS auth token", ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                ApsLog.Error("aps.auth", $"{(int)resp.StatusCode} {resp.ReasonPhrase} in {sw.ElapsedMilliseconds} ms scope={scopeLabel} body={Truncate(errBody)}");
                throw new HttpRequestException(
                    $"APS auth {(int)resp.StatusCode} {resp.ReasonPhrase}: " +
                    (string.IsNullOrWhiteSpace(errBody) ? "(no body)" : errBody.Trim()));
            }

            var token = await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("APS auth returned an empty body.");

            _cache[scope] = (token, DateTimeOffset.UtcNow.AddSeconds(token.ExpiresInSeconds - 60));
            ApsLog.Info("aps.auth", $"200 in {sw.ElapsedMilliseconds} ms scope={scopeLabel} ttl={token.ExpiresInSeconds}s");
            return token.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string ScopeLabel(string scope) => scope switch
    {
        "viewables:read" => "viewer",
        _ when scope.Contains("data:write") => "internal",
        _ => scope,
    };

    private static string Truncate(string s, int max = 300)
        => string.IsNullOrEmpty(s) ? "(empty)" : (s.Length > max ? s[..max] + "..." : s);
}
