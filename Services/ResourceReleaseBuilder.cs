using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyNotes.Services;

public static class ResourceReleaseBuilder
{
    public static string Build(string baseline, string package, string masterJson, string catalogName, string resourceVersion, string output)
    {
        baseline = Path.GetFullPath(baseline);
        package = Path.GetFullPath(package);
        masterJson = Path.GetFullPath(masterJson);
        output = Path.GetFullPath(output);
        if (!ResourcePaths.IsSegment(catalogName) || !ResourcePaths.IsSegment(resourceVersion))
            throw new InvalidDataException("invalid catalog or resource version");
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("output directory already exists");
        foreach (var input in new[] { baseline, package, masterJson })
        {
            var relative = Path.GetRelativePath(input, output);
            if (relative == "." || !Path.IsPathFullyQualified(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("output must be outside all input directories");
        }
        using var packageDoc = JsonDocument.Parse(File.ReadAllBytes(ResourcePaths.Resolve(package, "package.json")));
        var build = packageDoc.RootElement;
        if (build.GetProperty("format").GetString() != "nnnotes.song-build/1")
            throw new InvalidDataException("unsupported song package format");
        var version = build.GetProperty("version").GetString()!;
        if (!ResourcePaths.IsSegment(version))
            throw new InvalidDataException("invalid package version");
        var catalog = ResourcePaths.Resolve(baseline, $"Android/{catalogName}.bin");
        if (!Hash(catalog).Equals(build.GetProperty("baseline").GetProperty("catalogSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("package baseline catalog differs");
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in build.GetProperty("files").EnumerateArray())
        {
            var relative = entry.GetProperty("path").GetString()!;
            var expectedHash = entry.GetProperty("sha256").GetString()!;
            if (!listed.TryAdd(relative, expectedHash))
                throw new InvalidDataException("duplicate package file");
            var file = ResourcePaths.Resolve(package, relative);
            if (new FileInfo(file).Length != entry.GetProperty("size").GetInt64() ||
                !Hash(file).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("package file verification failed");
        }
        foreach (var file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(package, file).Replace('\\', '/');
            if (relative == "package.json" || relative == "validation.json")
                continue;
            if (!listed.ContainsKey(relative))
                throw new InvalidDataException("package contains an unlisted file");
        }
        foreach (var required in new[] { "catalog/catalog.bin", "catalog/catalog.hash", "master/MasterManifest.json" })
            if (!listed.ContainsKey(required))
                throw new InvalidDataException("required package file is missing");
        var masterVersions = Directory.EnumerateDirectories(ResourcePaths.Resolve(baseline, "master"))
            .Where(path => File.Exists(Path.Combine(path, "MasterManifest.json"))).ToArray();
        if (masterVersions.Length != 1)
            throw new InvalidDataException("baseline must contain exactly one master version");

        var staging = output + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        CopyTree(ResourcePaths.Resolve(baseline, "Android"), Path.Combine(staging, "Android"));
        var masterOut = Path.Combine(staging, "master", version);
        CopyTree(masterVersions[0], masterOut);
        CopyTree(Path.Combine(package, "master"), masterOut, overwrite: true);
        foreach (var bundle in build.GetProperty("bundles").EnumerateArray())
        {
            var file = ResourcePaths.Resolve(package, bundle.GetProperty("path").GetString()!);
            if (!listed.ContainsKey(bundle.GetProperty("path").GetString()!))
                throw new InvalidDataException("bundle is not listed in the package");
            var uri = new Uri(bundle.GetProperty("internal_id").GetString()!);
            if (Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()) != Path.GetFileName(file))
                throw new InvalidDataException("bundle filename differs from catalog URL");
            File.Copy(file, Path.Combine(staging, "Android", Path.GetFileName(file)), false);
            if (!Hash(Path.Combine(staging, "Android", Path.GetFileName(file))).Equals(
                listed[bundle.GetProperty("path").GetString()!], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("bundle changed during import");
        }
        File.Copy(ResourcePaths.Resolve(package, "catalog/catalog.bin"), Path.Combine(staging, "Android", catalogName + ".bin"), true);
        File.Copy(ResourcePaths.Resolve(package, "catalog/catalog.hash"), Path.Combine(staging, "Android", catalogName + ".hash"), true);
        var bareCatalog = Path.Combine(staging, "Android", catalogName);
        if (File.Exists(bareCatalog))
            File.Delete(bareCatalog);
        foreach (var (relative, expectedHash) in listed)
        {
            var destination = relative.StartsWith("master/", StringComparison.Ordinal) ?
                Path.Combine(masterOut, relative[7..].Replace('/', Path.DirectorySeparatorChar)) :
                relative == "catalog/catalog.bin" ? Path.Combine(staging, "Android", catalogName + ".bin") :
                relative == "catalog/catalog.hash" ? Path.Combine(staging, "Android", catalogName + ".hash") : null;
            if (destination != null && !Hash(destination).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("package changed during import");
        }
        CopyTree(masterJson, Path.Combine(staging, "master-json"));
        var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => new { path = Path.GetRelativePath(staging, path).Replace('\\', '/'),
                size = new FileInfo(path).Length, sha256 = Hash(path) }).ToArray();
        var release = new { format = "mynotes.resource-release/1", version, resourceVersion, catalogName,
            masterJsonDirectory = "master-json", files };
        var manifestPath = Path.Combine(staging, "release.json");
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(release, new JsonSerializerOptions { WriteIndented = true }));
        var environment = new ReleaseEnvironment(staging);
        var validated = new ResourceReleaseService(environment, manifestPath);
        _ = new MasterDataService(environment, NullLogger<MasterDataService>.Instance, validated);
        Directory.Move(staging, output);
        return Path.Combine(output, "release.json");
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void CopyTree(string source, string target, bool overwrite = false)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            var safe = ResourcePaths.Resolve(source, relative);
            var destination = ResourcePaths.Resolve(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(safe, destination, overwrite);
        }
    }

    private sealed class ReleaseEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "MyNotes";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
