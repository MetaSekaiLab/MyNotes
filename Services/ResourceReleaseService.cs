using System.Security.Cryptography;
using System.Text.Json;
using MyNotes.Config;

namespace MyNotes.Services;

public sealed class ResourceReleaseService
{
    private readonly Dictionary<string, FileEntry> _files = new(StringComparer.Ordinal);
    private readonly string? _root;
    public string MasterVersion { get; }
    public string ResourceVersion { get; }
    public string? MasterJsonDirectory { get; }
    public string? CatalogName { get; }
    public bool Enabled => _root != null;

    public ResourceReleaseService(IHostEnvironment environment) : this(environment, ServerConfig.ResourceReleaseManifest) { }

    public ResourceReleaseService(IHostEnvironment environment, string manifestPath)
    {
        MasterVersion = ServerConfig.MasterVersion;
        ResourceVersion = ServerConfig.ResourceVersion;
        if (string.IsNullOrWhiteSpace(manifestPath))
            return;

        var path = Path.GetFullPath(manifestPath, environment.ContentRootPath);
        _root = Path.GetDirectoryName(path)!;
        _ = ResourcePaths.Resolve(_root, Path.GetFileName(path));
        using var source = JsonDocument.Parse(File.ReadAllBytes(path));
        var doc = source.RootElement;
        if (doc.GetProperty("format").GetString() != "mynotes.resource-release/1")
            throw new InvalidDataException("unsupported resource release format");
        MasterVersion = Segment(doc, "version");
        ResourceVersion = Segment(doc, "resourceVersion");
        CatalogName = Segment(doc, "catalogName");
        foreach (var entry in doc.GetProperty("files").EnumerateArray())
        {
            var relative = entry.GetProperty("path").GetString()!;
            var file = ResourcePaths.Resolve(_root, relative);
            var hash = entry.GetProperty("sha256").GetString()!;
            var size = entry.GetProperty("size").GetInt64();
            if (size < 0 || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)) || !_files.TryAdd(relative, new(file, size, hash)))
                throw new InvalidDataException("invalid or duplicate release file entry");
            using var stream = File.OpenRead(file);
            if (stream.Length != size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("resource release file verification failed");
        }

        var masterPrefix = $"master/{MasterVersion}/";
        var manifest = Required(masterPrefix + "MasterManifest.json");
        using var manifestSource = JsonDocument.Parse(File.ReadAllBytes(manifest.Path));
        var master = manifestSource.RootElement;
        if (master.GetProperty("version").GetString() != MasterVersion)
            throw new InvalidDataException("master manifest version differs from release version");
        var masterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in master.GetProperty("files").EnumerateArray())
        {
            var name = Segment(entry, "name");
            if (!masterNames.Add(name))
                throw new InvalidDataException("duplicate master file");
            var file = Required(masterPrefix + name);
            if (file.Size != entry.GetProperty("size").GetInt64() ||
                !file.Hash.Equals(entry.GetProperty("hash").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("master manifest file differs from release entry");
        }
        var catalog = Required($"Android/{CatalogName}.bin");
        var marker = Required($"Android/{CatalogName}.hash");
        if (File.ReadAllText(marker.Path).Trim() != catalog.Hash[..32].ToLowerInvariant())
            throw new InvalidDataException("catalog marker differs from catalog content");

        if (doc.TryGetProperty("masterJsonDirectory", out var jsonPath))
        {
            var relative = jsonPath.GetString()!;
            MasterJsonDirectory = ResourcePaths.Resolve(_root, relative);
            foreach (var name in new[] { "MasterPlayerData", "MasterMemberCard", "MasterSupportCard", "MasterLiveMusic", "MasterLiveMusicScore", "MasterLiveMusicComboReward" })
            {
                var file = Required($"{relative}/{name}.json");
                using var table = JsonDocument.Parse(File.ReadAllBytes(file.Path));
                if (table.RootElement.GetProperty("_allData").ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("master JSON tables require an array of rows");
            }
        }
    }

    public string? FindAndroid(string name)
    {
        if (!Enabled)
            return null;
        var key = name == CatalogName ? $"Android/{CatalogName}.bin" : "Android/" + name;
        return _files.TryGetValue(key, out var file) ? file.Path : null;
    }

    public string? FindMaster(string version, string name) => Enabled && version == MasterVersion &&
        _files.TryGetValue($"master/{version}/{name}", out var file) ? file.Path : null;

    private FileEntry Required(string relative) => _files.TryGetValue(relative, out var file) ? file :
        throw new InvalidDataException("required release file is missing");

    private static string Segment(JsonElement doc, string property)
    {
        var value = doc.GetProperty(property).GetString() ?? "";
        return ResourcePaths.IsSegment(value) ? value : throw new InvalidDataException("invalid release identity");
    }

    private sealed record FileEntry(string Path, long Size, string Hash);
}
