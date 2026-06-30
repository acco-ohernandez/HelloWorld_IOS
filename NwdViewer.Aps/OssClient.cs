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
        using var resp = await ApsHttp.SendAsync(_http, req, ApsHttp.ShortCallTimeout, "APS bucket-create", ct);
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

    // APS Direct-to-S3 multipart upload. S3 requires every part except the last to be >= 5 MB.
    private const long ChunkSize = 10L * 1024 * 1024;   // 10 MB parts
    private const int MaxPartRetries = 3;               // per-part attempts (fresh signed URL each retry)
    private const int UrlExpiryMinutes = 60;            // max APS allows for signed-S3 URLs

    /// <summary>
    /// Uploads <paramref name="localPath"/> to OSS via APS's Direct-to-S3 signed upload, chunked
    /// into multiple parts so a large file survives a slow/flaky connection (the old single PUT of
    /// the whole object timed out on 5G). Each part is retried with a fresh signed URL; progress is
    /// reported per completed part. Returns the object URN.
    /// </summary>
    public async Task<string> UploadAsync(
        string localPath,
        string objectKey,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var token = await _auth.GetInternalTokenAsync(ct);
        var fileSize = new FileInfo(localPath).Length;
        var partCount = (int)Math.Max(1, (fileSize + ChunkSize - 1) / ChunkSize);
        ApsLog.Info("aps.upload", $"{objectKey} starting · {FormatBytes(fileSize)} in {partCount} part(s) bucket={_options.BucketKey}");
        var totalSw = Stopwatch.StartNew();

        var signedBase = $"{_options.BaseUrl}/oss/v2/buckets/{Uri.EscapeDataString(_options.BucketKey)}/objects/{Uri.EscapeDataString(objectKey)}/signeds3upload";

        // Step 1: ask APS for a batch of signed S3 part URLs + the uploadKey that ties them together.
        var initSw = Stopwatch.StartNew();
        var init = await GetSignedUrlsAsync(signedBase, token, partCount, firstPart: 1, uploadKey: null, ct);
        initSw.Stop();
        if (init.Urls.Count < partCount)
            throw new InvalidOperationException($"APS signeds3upload returned {init.Urls.Count} URL(s) for {partCount} part(s).");
        ApsLog.Info("aps.upload", $"signed-URLs acquired ({partCount} part(s) in {initSw.ElapsedMilliseconds} ms)");

        // Step 2: PUT each chunk, retrying with a fresh URL per part on transient failure / URL expiry.
        var putSw = Stopwatch.StartNew();
        await using (var fs = File.OpenRead(localPath))
        {
            var buffer = new byte[ChunkSize];
            for (var part = 0; part < partCount; part++)
            {
                var offset = (long)part * ChunkSize;
                var chunkLen = (int)Math.Min(ChunkSize, fileSize - offset);
                fs.Position = offset;
                await fs.ReadExactlyAsync(buffer.AsMemory(0, chunkLen), ct);

                await UploadPartWithRetryAsync(signedBase, token, init, part, buffer, chunkLen, ct);

                // Per-part progress (kept under 100; the complete step + caller cap finish it off).
                progress?.Report((int)Math.Min(99, (offset + chunkLen) * 100.0 / Math.Max(1, fileSize)));
            }
        }
        putSw.Stop();
        var throughput = fileSize > 0 && putSw.ElapsedMilliseconds > 0
            ? $" · {FormatBytes((long)(fileSize * 1000.0 / putSw.ElapsedMilliseconds))}/s"
            : "";
        ApsLog.Info("aps.upload", $"S3 parts done ({partCount} in {putSw.ElapsedMilliseconds} ms{throughput})");

        // Step 3: tell APS the upload finished so it materializes the OSS object.
        using var completeReq = new HttpRequestMessage(HttpMethod.Post, signedBase)
        {
            Content = JsonContent.Create(new { uploadKey = init.UploadKey })
        };
        completeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var compSw = Stopwatch.StartNew();
        using var completeResp = await ApsHttp.SendAsync(_http, completeReq, ApsHttp.ShortCallTimeout, "APS signeds3upload complete", ct);
        compSw.Stop();
        if (!completeResp.IsSuccessStatusCode)
            ApsLog.Error("aps.upload", $"complete failed {(int)completeResp.StatusCode} in {compSw.ElapsedMilliseconds} ms");
        await EnsureSuccessOrThrowAsync(completeResp, "APS signeds3upload complete", ct);
        var completed = await completeResp.Content.ReadFromJsonAsync<SignedS3UploadComplete>(cancellationToken: ct)
            ?? throw new InvalidOperationException("APS signeds3upload complete returned empty payload.");

        progress?.Report(100);
        totalSw.Stop();
        var urn = ToUrn(completed.ObjectId);
        ApsLog.Info("aps.upload", $"complete ({(int)completeResp.StatusCode} in {compSw.ElapsedMilliseconds} ms) total={totalSw.ElapsedMilliseconds} ms urn={urn}");
        return urn;
    }

    // GET signeds3upload for `parts` URLs starting at `firstPart`. Pass an existing uploadKey to
    // extend the same upload session (used to re-mint a single expired/failed part URL).
    private async Task<SignedS3UploadInit> GetSignedUrlsAsync(
        string signedBase, string token, int parts, int firstPart, string? uploadKey, CancellationToken ct)
    {
        var url = $"{signedBase}?parts={parts}&firstPart={firstPart}&minutesExpiration={UrlExpiryMinutes}";
        if (uploadKey is not null) url += $"&uploadKey={Uri.EscapeDataString(uploadKey)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await ApsHttp.SendAsync(_http, req, ApsHttp.ShortCallTimeout, "APS signeds3upload init", ct);
        await EnsureSuccessOrThrowAsync(resp, "APS signeds3upload init", ct);
        return await resp.Content.ReadFromJsonAsync<SignedS3UploadInit>(cancellationToken: ct)
            ?? throw new InvalidOperationException("APS signeds3upload returned empty init payload.");
    }

    private async Task UploadPartWithRetryAsync(
        string signedBase, string token, SignedS3UploadInit init, int partIndex,
        byte[] buffer, int chunkLen, CancellationToken ct)
    {
        var url = init.Urls[partIndex];
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var putReq = new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = new ByteArrayContent(buffer, 0, chunkLen),
                };
                putReq.Content.Headers.ContentLength = chunkLen;
                using var putResp = await ApsHttp.SendAsync(
                    _http, putReq, ApsHttp.UploadTimeoutFor(chunkLen), $"APS signed-S3 PUT part {partIndex + 1}", ct);
                if (putResp.IsSuccessStatusCode) return;
                await EnsureSuccessOrThrowAsync(putResp, $"APS signed-S3 PUT part {partIndex + 1}", ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < MaxPartRetries)
            {
                ApsLog.Warn("aps.upload", $"part {partIndex + 1} attempt {attempt} failed ({ex.GetType().Name}); re-minting URL and retrying");
                // The signed URL may have expired or the socket dropped — re-mint a fresh URL for
                // just this part under the same uploadKey, then retry.
                var refreshed = await GetSignedUrlsAsync(signedBase, token, parts: 1, firstPart: partIndex + 1, init.UploadKey, ct);
                if (refreshed.Urls.Count > 0) url = refreshed.Urls[0];
            }
        }
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
