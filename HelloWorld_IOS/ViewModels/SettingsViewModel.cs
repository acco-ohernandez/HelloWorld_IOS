using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using HelloWorld_IOS.Services;

namespace HelloWorld_IOS.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly CredentialStore _credentials;

    // APS bucket key rules: lowercase letters, digits, underscores only.
    // 3-128 chars total. https://aps.autodesk.com/en/docs/data/v2/reference/http/buckets-POST/
    private static readonly Regex BucketKeyRegex = new(@"^[a-z0-9_]{3,128}$", RegexOptions.Compiled);

    [ObservableProperty] private string clientId = string.Empty;
    [ObservableProperty] private string clientSecret = string.Empty;
    [ObservableProperty] private string bucketKey = string.Empty;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private bool isBusy;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        IsValidBucketKey(BucketKey);

    public static bool IsValidBucketKey(string? key)
        => !string.IsNullOrWhiteSpace(key) && BucketKeyRegex.IsMatch(key);

    public SettingsViewModel(CredentialStore credentials)
    {
        _credentials = credentials;
    }

    /// <summary>Pre-fill the form from existing keychain entries.</summary>
    public async Task LoadAsync()
    {
        var creds = await _credentials.LoadAsync();
        if (creds is null) return;
        ClientId = creds.ClientId;
        ClientSecret = creds.ClientSecret;
        BucketKey = creds.BucketKey;
    }

    /// <summary>Persist the form to keychain. Returns true on success.</summary>
    public async Task<bool> SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            StatusText = "Client ID and Client Secret are required.";
            return false;
        }
        if (!IsValidBucketKey(BucketKey))
        {
            StatusText = "Bucket key must be 3-128 chars: lowercase letters, digits, and underscores only.";
            return false;
        }
        try
        {
            IsBusy = true;
            // Log non-secret deltas: client-id prefix + full bucket key + whether
            // secret was set. Never the full secret or full id.
            var prior = await _credentials.LoadAsync();
            var idPrefix = ClientId.Length > 6 ? ClientId[..6] + "..." : ClientId;
            var bucketChanged = prior is null || prior.BucketKey != BucketKey.Trim();
            var clientIdChanged = prior is null || prior.ClientId != ClientId.Trim();
            var secretChanged = prior is null || prior.ClientSecret != ClientSecret.Trim();
            Logger.Info("app.settings",
                $"saving · clientId={idPrefix} (changed={clientIdChanged}) · " +
                $"bucket={BucketKey.Trim()} (changed={bucketChanged}) · " +
                $"secretChanged={secretChanged}");

            await _credentials.SaveAsync(new ApsCredentials(
                ClientId.Trim(), ClientSecret.Trim(), BucketKey.Trim()));
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
            Logger.Error("app.settings", "save failed", ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnClientIdChanged(string value)     => StatusText = string.Empty;
    partial void OnClientSecretChanged(string value) => StatusText = string.Empty;
    partial void OnBucketKeyChanged(string value)    => StatusText = string.Empty;
}
