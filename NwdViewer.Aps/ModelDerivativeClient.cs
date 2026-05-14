using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NwdViewer.Aps;

public sealed class ModelDerivativeClient
{
    private readonly HttpClient _http;
    private readonly AuthClient _auth;
    private readonly ApsOptions _options;

    public ModelDerivativeClient(HttpClient http, AuthClient auth, ApsOptions options)
    {
        _http = http;
        _auth = auth;
        _options = options;
    }

    // forceRetranslate=true sets x-ads-force, which makes APS re-run the policy gate
    // and burn credits even if a manifest already exists. Default off; flip on only
    // when intentionally reprocessing.
    public async Task StartTranslationAsync(string urn, bool forceRetranslate = false, CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        var jobUrn = OssClient.WithoutPrefix(urn);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/modelderivative/v2/designdata/job")
        {
            Content = JsonContent.Create(new
            {
                input = new { urn = jobUrn },   // POST body wants just the base64, no "urn:" prefix
                output = new
                {
                    formats = new[]
                    {
                        new { type = "svf2", views = new[] { "2d", "3d" } }
                    }
                }
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (forceRetranslate)
            req.Headers.Add("x-ads-force", "true");

        ApsLog.Info("aps.translate", $"POST job force={forceRetranslate} urn={UrnPreview(jobUrn)}");
        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        sw.Stop();
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            ApsLog.Error("aps.translate", $"{(int)resp.StatusCode} {resp.ReasonPhrase} in {sw.ElapsedMilliseconds} ms");
            throw new HttpRequestException(
                $"APS translation job POST failed with {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
        }
        ApsLog.Info("aps.translate", $"{(int)resp.StatusCode} accepted in {sw.ElapsedMilliseconds} ms");
    }

    public async Task<TranslationManifest> GetManifestAsync(string urn, CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_options.BaseUrl}/modelderivative/v2/designdata/{Uri.EscapeDataString(OssClient.WithoutPrefix(urn))}/manifest");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"APS manifest GET failed with {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
        }
        return await resp.Content.ReadFromJsonAsync<TranslationManifest>(cancellationToken: ct)
            ?? throw new InvalidOperationException("APS returned an empty manifest.");
    }

    public async Task<TranslationManifest> WaitForTranslationAsync(
        string urn,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var pollSw = Stopwatch.StartNew();
        var poll = 0;
        string? lastStatus = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            poll++;
            TranslationManifest manifest;
            try
            {
                manifest = await GetManifestAsync(urn, ct);
            }
            catch (HttpRequestException ex) when (poll == 1 && ex.Message.Contains("404"))
            {
                // APS hasn't begun indexing on the very first poll; tolerated.
                ApsLog.Warn("aps.manifest", $"poll {poll} returned 404 (translation not yet indexed; will retry)");
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                continue;
            }

            var pctText = manifest.Progress ?? "?";
            if (int.TryParse(manifest.Progress?.Replace("%", "").Replace("complete", "").Trim(), out var pct))
                progress?.Report(pct);

            // Log status transitions (not every poll - that would spam) and the first poll.
            if (poll == 1 || manifest.Status != lastStatus)
                ApsLog.Info("aps.manifest", $"poll {poll} t={pollSw.Elapsed.TotalSeconds:F1}s status={manifest.Status} progress={pctText}");
            lastStatus = manifest.Status;

            switch (manifest.Status)
            {
                case "success":
                    progress?.Report(100);
                    ApsLog.Info("aps.manifest", $"translation success after {poll} polls in {pollSw.Elapsed.TotalSeconds:F1}s");
                    return manifest;
                case "failed":
                case "timeout":
                    ApsLog.Error("aps.manifest", $"translation {manifest.Status} after {poll} polls in {pollSw.Elapsed.TotalSeconds:F1}s");
                    throw new InvalidOperationException($"APS translation {manifest.Status} for urn {urn}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }
    }

    public async Task<List<MetadataEntry>> GetMetadataAsync(string urn, CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_options.BaseUrl}/modelderivative/v2/designdata/{Uri.EscapeDataString(OssClient.WithoutPrefix(urn))}/metadata");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        sw.Stop();
        if (!resp.IsSuccessStatusCode)
        {
            ApsLog.Error("aps.metadata", $"{(int)resp.StatusCode} in {sw.ElapsedMilliseconds} ms");
            resp.EnsureSuccessStatusCode();
        }
        var wrapper = await resp.Content.ReadFromJsonAsync<MetadataListResponse>(cancellationToken: ct);
        var entries = wrapper?.Data?.Metadata ?? new List<MetadataEntry>();
        ApsLog.Info("aps.metadata", $"{(int)resp.StatusCode} in {sw.ElapsedMilliseconds} ms · {entries.Count} entries");
        return entries;
    }

    /// <summary>Short URN preview for log readability (full URN is too long for grep-ability).</summary>
    private static string UrnPreview(string urn)
        => urn.Length <= 40 ? urn : $"{urn[..20]}...{urn[^16..]}";

    public async Task<ObjectProperties?> GetObjectPropertiesAsync(
        string urn,
        string modelGuid,
        int objectId,
        CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_options.BaseUrl}/modelderivative/v2/designdata/{Uri.EscapeDataString(OssClient.WithoutPrefix(urn))}/metadata/{Uri.EscapeDataString(modelGuid)}/properties?objectid={objectId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"APS properties GET failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");

        // Stash the raw body so the caller can log it if deserialization produces
        // a null Data/Collection — APS sometimes returns 200 with a body that's
        // still indexing and doesn't have the { data: { collection: [...] } } shape.
        LastPropertiesRawBody = body;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ObjectProperties>(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Raw body of the most recent GetObjectPropertiesAsync call; for diagnostics when
    /// the typed response is unexpectedly empty.</summary>
    public string? LastPropertiesRawBody { get; private set; }
}
