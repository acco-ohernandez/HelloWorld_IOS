using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace NwdViewer.Aps;

public sealed class OssClient
{
    private readonly HttpClient _http;
    private readonly AuthClient _auth;
    private readonly ApsOptions _options;

    public OssClient(HttpClient http, AuthClient auth, ApsOptions options)
    {
        _http = http;
        _auth = auth;
        _options = options;
    }

    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/oss/v2/buckets")
        {
            Content = JsonContent.Create(new { bucketKey = _options.BucketKey, policyKey = "transient" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        sw.Stop();

        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            ApsLog.Info("aps.bucket", $"{_options.BucketKey} already exists (409 in {sw.ElapsedMilliseconds} ms)");
            return;
        }
        if (!resp.IsSuccessStatusCode)
        {
            ApsLog.Error("aps.bucket", $"create failed {(int)resp.StatusCode} in {sw.ElapsedMilliseconds} ms key={_options.BucketKey}");
            await EnsureSuccessOrThrowAsync(resp, $"APS bucket-create (key='{_options.BucketKey}')", ct);
            return;
        }
        ApsLog.Info("aps.bucket", $"created {_options.BucketKey} ({(int)resp.StatusCode} in {sw.ElapsedMilliseconds} ms)");
    }

    public async Task<string> UploadAsync(
        string localPath,
        string objectKey,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        var fileSize = new FileInfo(localPath).Length;
        ApsLog.Info("aps.upload", $"{objectKey} starting · {FormatBytes(fileSize)} bucket={_options.BucketKey}");
        var totalSw = Stopwatch.StartNew();

        // Step 1: ask APS for a signed S3 URL.
        var initUrl = $"{_options.BaseUrl}/oss/v2/buckets/{Uri.EscapeDataString(_options.BucketKey)}/objects/{Uri.EscapeDataString(objectKey)}/signeds3upload";
        using var initReq = new HttpRequestMessage(HttpMethod.Get, initUrl);
        initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var sw = Stopwatch.StartNew();
        using var initResp = await _http.SendAsync(initReq, ct);
        sw.Stop();
        if (!initResp.IsSuccessStatusCode)
            ApsLog.Error("aps.upload", $"signed-URL init failed {(int)initResp.StatusCode} in {sw.ElapsedMilliseconds} ms");
        else
            ApsLog.Info("aps.upload", $"signed-URL acquired ({(int)initResp.StatusCode} in {sw.ElapsedMilliseconds} ms)");
        await EnsureSuccessOrThrowAsync(initResp, "APS signeds3upload init", ct);
        var init = await initResp.Content.ReadFromJsonAsync<SignedS3UploadInit>(cancellationToken: ct)
            ?? throw new InvalidOperationException("APS signeds3upload returned empty init payload.");

        if (init.Urls.Count == 0)
            throw new InvalidOperationException("APS signeds3upload returned no upload URLs.");

        // Step 2: PUT the file bytes to S3.
        await using (var fs = File.OpenRead(localPath))
        {
            using var putReq = new HttpRequestMessage(HttpMethod.Put, init.Urls[0])
            {
                Content = new StreamContent(fs),
            };
            putReq.Content.Headers.ContentLength = fileSize;

            sw.Restart();
            using var putResp = await _http.SendAsync(putReq, ct);
            sw.Stop();
            if (!putResp.IsSuccessStatusCode)
                ApsLog.Error("aps.upload", $"S3 PUT failed {(int)putResp.StatusCode} in {sw.ElapsedMilliseconds} ms");
            else
            {
                var throughput = fileSize > 0 && sw.ElapsedMilliseconds > 0
                    ? $" · {FormatBytes((long)(fileSize * 1000.0 / sw.ElapsedMilliseconds))}/s"
                    : "";
                ApsLog.Info("aps.upload", $"S3 PUT done ({(int)putResp.StatusCode} in {sw.ElapsedMilliseconds} ms{throughput})");
            }
            await EnsureSuccessOrThrowAsync(putResp, "APS signed-S3 PUT", ct);
            progress?.Report(90);
        }

        // Step 3: tell APS the upload finished so it materializes the OSS object.
        var completeUrl = $"{_options.BaseUrl}/oss/v2/buckets/{Uri.EscapeDataString(_options.BucketKey)}/objects/{Uri.EscapeDataString(objectKey)}/signeds3upload";
        using var completeReq = new HttpRequestMessage(HttpMethod.Post, completeUrl)
        {
            Content = JsonContent.Create(new { uploadKey = init.UploadKey })
        };
        completeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        sw.Restart();
        using var completeResp = await _http.SendAsync(completeReq, ct);
        sw.Stop();
        if (!completeResp.IsSuccessStatusCode)
            ApsLog.Error("aps.upload", $"complete failed {(int)completeResp.StatusCode} in {sw.ElapsedMilliseconds} ms");
        await EnsureSuccessOrThrowAsync(completeResp, "APS signeds3upload complete", ct);
        var completed = await completeResp.Content.ReadFromJsonAsync<SignedS3UploadComplete>(cancellationToken: ct)
            ?? throw new InvalidOperationException("APS signeds3upload complete returned empty payload.");

        progress?.Report(100);
        totalSw.Stop();
        var urn = ToUrn(completed.ObjectId);
        ApsLog.Info("aps.upload", $"complete ({(int)completeResp.StatusCode} in {sw.ElapsedMilliseconds} ms) total={totalSw.ElapsedMilliseconds} ms urn={urn}");
        return urn;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    /// <summary>
    /// APS "URN" form: URL-safe base64 of the object ID with a "urn:" prefix. The prefix
    /// is kept for URL path usage (/manifest, /metadata). Strip it with <see cref="WithoutPrefix"/>
    /// when putting the URN into JSON request bodies — the Model Derivative API rejects it there.
    /// </summary>
    private static string ToUrn(string objectId)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(objectId))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "urn:" + b64;
    }

    public static string WithoutPrefix(string urn)
        => urn.StartsWith("urn:", StringComparison.Ordinal) ? urn[4..] : urn;

    /// <summary>
    /// Like <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> but the
    /// thrown <see cref="HttpRequestException"/> message includes the response
    /// body so APS error reasons (e.g., "BucketKey is invalid") surface to
    /// callers instead of just the bare status code.
    /// </summary>
    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage resp, string operation, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = string.Empty;
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* best-effort */ }
        if (!string.IsNullOrWhiteSpace(body) && body.Length > 600) body = body.Substring(0, 600) + "...";
        throw new HttpRequestException(
            $"{operation} {(int)resp.StatusCode} {resp.ReasonPhrase}: " +
            (string.IsNullOrWhiteSpace(body) ? "(no body)" : body.Trim()));
    }
}
