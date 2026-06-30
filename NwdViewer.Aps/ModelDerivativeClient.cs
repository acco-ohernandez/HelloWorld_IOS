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
                        // 3d only: this is a 3D model viewer with no 2d-sheet UI, so requesting
                        // "2d" only made translation heavier/slower for no benefit.
                        new { type = "svf2", views = new[] { "3d" } }
                    }
                }
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (forceRetranslate)
            req.Headers.Add("x-ads-force", "true");

        ApsLog.Info("aps.translate", $"POST job force={forceRetranslate} urn={UrnPreview(jobUrn)}");
        var sw = Stopwatch.StartNew();
        using var resp = await ApsHttp.SendAsync(_http, req, ApsHttp.ShortCallTimeout, "APS translation job POST", ct);
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

        using var resp = await ApsHttp.SendAsync(_http, req, ApsHttp.ShortCallTimeout, "APS manifest GET", ct);
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

        // Exponential backoff between polls: start tight so a cache hit / fast translate is snappy,
        // then ease off so a 40-min federated-NWD translate doesn't fire ~600 polls (and ~600
        // matching auth cache-hit log lines) like the old fixed 4 s interval did.
        var delay = MinPollDelay;
        // Tolerate a few consecutive transient manifest failures (a flaky-link timeout or 5xx)
        // mid-translate instead of aborting a translation that is still running server-side.
        var consecutiveErrors = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (pollSw.Elapsed > MaxTranslationWait)
            {
                ApsLog.Error("aps.manifest", $"translation still '{lastStatus ?? "unknown"}' after {pollSw.Elapsed.TotalMinutes:F0} min ({poll} polls); giving up");
                throw new TranslationTimeoutException(
                    $"APS translation did not finish within {MaxTranslationWait.TotalMinutes:F0} minutes (last status: {lastStatus ?? "unknown"}).");
            }
            poll++;
            TranslationManifest manifest;
            try
            {
                manifest = await GetManifestAsync(urn, ct);
                consecutiveErrors = 0;
            }
            catch (HttpRequestException ex) when (poll == 1 && ex.Message.Contains("404"))
            {
                // APS hasn't begun indexing on the very first poll; tolerated.
                ApsLog.Warn("aps.manifest", $"poll {poll} returned 404 (translation not yet indexed; will retry)");
                await DelayAndBackoff();
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException && !ct.IsCancellationRequested)
            {
                consecutiveErrors++;
                if (consecutiveErrors >= MaxConsecutivePollErrors)
                {
                    ApsLog.Error("aps.manifest", $"poll {poll} failed {consecutiveErrors}x consecutively; giving up ({ex.GetType().Name}: {ex.Message})");
                    throw;
                }
                ApsLog.Warn("aps.manifest", $"poll {poll} transient error ({ex.GetType().Name}); retry {consecutiveErrors}/{MaxConsecutivePollErrors}");
                await DelayAndBackoff();
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

            await DelayAndBackoff();
        }

        async Task DelayAndBackoff()
        {
            await Task.Delay(delay, ct);
            var next = delay.TotalSeconds * PollBackoffFactor;
            delay = TimeSpan.FromSeconds(Math.Min(next, MaxPollDelay.TotalSeconds));
        }
    }

    private static readonly TimeSpan MinPollDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxPollDelay = TimeSpan.FromSeconds(15);
    private const double PollBackoffFactor = 1.5;
    // Generous cap: the reference 476 MB federated NWD took ~44 min, so 120 min leaves headroom.
    private static readonly TimeSpan MaxTranslationWait = TimeSpan.FromMinutes(120);
    private const int MaxConsecutivePollErrors = 5;

    public async Task<List<MetadataEntry>> GetMetadataAsync(string urn, CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_options.BaseUrl}/modelderivative/v2/designdata/{Uri.EscapeDataString(OssClient.WithoutPrefix(urn))}/metadata");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var sw = Stopwatch.StartNew();
        using var resp = await ApsHttp.SendAsync(_http, req, ApsHttp.ShortCallTimeout, "APS metadata GET", ct);
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

        using var resp = await ApsHttp.SendAsync(_http, req, ApsHttp.ShortCallTimeout, "APS properties GET", ct);
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
