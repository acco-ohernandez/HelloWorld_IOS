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
            if (_cache.TryGetValue(scope, out var entry) && DateTimeOffset.UtcNow < entry.ExpiresAt)
                return entry.Token.AccessToken;

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

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"APS auth {(int)resp.StatusCode} {resp.ReasonPhrase}: " +
                    (string.IsNullOrWhiteSpace(errBody) ? "(no body)" : errBody.Trim()));
            }

            var token = await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("APS auth returned an empty body.");

            _cache[scope] = (token, DateTimeOffset.UtcNow.AddSeconds(token.ExpiresInSeconds - 60));
            return token.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}
