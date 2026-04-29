namespace HelloWorld_IOS.Services;

// v2 seam: thin SecureStorage wrapper exposing the same shape as the WPF
// CredentialStore (CredentialManagement-backed). Not called from v1 code.
// When NwdViewer.Aps is ported, the existing ApsServices constructor reads
// this same record without any wiring change.
public sealed class CredentialStore
{
    private const string KeyClientId     = "NwdViewer.ApsClientId";
    private const string KeyClientSecret = "NwdViewer.ApsClientSecret";
    private const string KeyBucket       = "NwdViewer.ApsBucketKey";

    public bool HasCredentials => SecureStorage.Default.GetAsync(KeyClientId).Result is not null;

    public async Task<ApsCredentials?> LoadAsync()
    {
        var id = await SecureStorage.Default.GetAsync(KeyClientId);
        var secret = await SecureStorage.Default.GetAsync(KeyClientSecret);
        var bucket = await SecureStorage.Default.GetAsync(KeyBucket);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(bucket))
            return null;
        return new ApsCredentials(id, secret, bucket);
    }

    public async Task SaveAsync(ApsCredentials creds)
    {
        await SecureStorage.Default.SetAsync(KeyClientId, creds.ClientId);
        await SecureStorage.Default.SetAsync(KeyClientSecret, creds.ClientSecret);
        await SecureStorage.Default.SetAsync(KeyBucket, creds.BucketKey);
    }

    public void Delete()
    {
        SecureStorage.Default.Remove(KeyClientId);
        SecureStorage.Default.Remove(KeyClientSecret);
        SecureStorage.Default.Remove(KeyBucket);
    }
}

public sealed record ApsCredentials(string ClientId, string ClientSecret, string BucketKey);
