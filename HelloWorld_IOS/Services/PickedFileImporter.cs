namespace HelloWorld_IOS.Services;

// Copies a FilePicker / drag-drop result into the per-tab cache folder so that
// the URL scheme handler can serve it. iOS picker results are security-scoped:
// FileResult.OpenReadAsync() handles the scope under the hood, so we just stream.
public sealed class PickedFileImporter
{
    private readonly TabFileStore _store;

    public PickedFileImporter(TabFileStore store)
    {
        _store = store;
    }

    public async Task<string> ImportAsync(FileResult result, int tabId, CancellationToken ct = default)
    {
        var tabDir = _store.EnsureTabDirectory(tabId);
        var safeName = Path.GetFileName(result.FileName);
        var dest = Path.Combine(tabDir, safeName);

        await using var src = await result.OpenReadAsync();
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct);
        return dest;
    }

    public static readonly string[] SupportedExtensions =
        [".ifc", ".gltf", ".glb", ".obj", ".mtl", ".fbx", ".stl", ".bin", ".png", ".jpg", ".jpeg"];

    public static bool IsModelExtension(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext is ".ifc" or ".gltf" or ".glb" or ".obj" or ".fbx" or ".stl";
    }

    public static string FormatFromExtension(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext switch
        {
            ".gltf" => "gltf",
            ".glb"  => "glb",
            ".obj"  => "obj",
            ".fbx"  => "fbx",
            ".stl"  => "stl",
            ".ifc"  => "ifc",
            _       => string.Empty,
        };
    }
}
