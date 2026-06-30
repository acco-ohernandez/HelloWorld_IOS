namespace NwdViewer.Aps;

/// <summary>
/// HTTP timeout helpers for the APS clients.
///
/// The shared <see cref="HttpClient"/> runs with <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
/// (set in the host's ApsServices facade) because a single global cap cannot suit both a
/// multi-minute large-file upload and a sub-second token call. Instead every logical call applies
/// its own timeout here via a linked <see cref="CancellationTokenSource"/>.
///
/// On timeout (when the caller's own token was NOT cancelled) we throw a <see cref="TimeoutException"/>
/// with a clear message, so the host can classify a client-side network timeout separately from a
/// user cancellation and from a real APS HTTP error.
/// </summary>
public static class ApsHttp
{
    /// <summary>Per-request timeout for short APS calls (auth, translate POST, metadata, a single manifest poll).</summary>
    public static readonly TimeSpan ShortCallTimeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Floor throughput used to derive a size-aware upload timeout. A genuinely alive-but-slow
    /// cellular link stays under the cap; a dead socket still trips it. ~200 KB/s → a 476 MB
    /// file gets ~40 min before timing out, versus the old flat 600 s that killed it on 5G.
    /// </summary>
    private const double MinUploadBytesPerSecond = 200 * 1024;

    public static TimeSpan UploadTimeoutFor(long bytes)
        => TimeSpan.FromSeconds(Math.Max(120.0, bytes / MinUploadBytesPerSecond));

    /// <summary>
    /// Sends <paramref name="req"/> on <paramref name="http"/> with <paramref name="timeout"/> layered
    /// on top of the caller's <paramref name="ct"/>. The request is sent once (no retry). On timeout
    /// throws <see cref="TimeoutException"/>; a genuine user cancellation propagates as
    /// <see cref="OperationCanceledException"/> unchanged.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        HttpRequestMessage req,
        TimeSpan timeout,
        string operation,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await http.SendAsync(req, completion, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our timeout fired, not the caller. Surface a typed, readable error.
            throw new TimeoutException(
                $"{operation} timed out after {timeout.TotalSeconds:F0}s with no response from the server.");
        }
    }
}

/// <summary>
/// Thrown by <see cref="ModelDerivativeClient.WaitForTranslationAsync"/> when a translation has not
/// reached a terminal state within the maximum wait window. Distinct from a per-request
/// <see cref="TimeoutException"/> so callers can message it as "still processing" rather than "network".
/// </summary>
public sealed class TranslationTimeoutException : Exception
{
    public TranslationTimeoutException(string message) : base(message) { }
}
