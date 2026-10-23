using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MyNotes.Services;
using Xunit;

namespace MyNotes.Tests;

public sealed class ResourceBuilderTests
{
    [Fact]
    public void OverlayBuildKeepsUnchangedMasterFilesAndPublishesCatalogPair()
    {
        using var fixture = new SyntheticSongPackageFixture();

        var path = fixture.Build();
        var release = new ResourceReleaseService(new BuilderEnvironment(fixture.Output), path);

        Assert.Equal(Path.Combine(fixture.Output, "release.json"), path);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(release.FindAndroid("new.bundle")!));
        Assert.Equal(File.ReadAllBytes(fixture.Baseline.FilePath("Android/sample.bundle")),
            File.ReadAllBytes(release.FindAndroid("sample.bundle")!));
        Assert.Equal(File.ReadAllBytes(fixture.Baseline.FilePath(fixture.Baseline.MasterFilePath)),
            File.ReadAllBytes(release.FindMaster(ResourceReleaseFixture.MasterVersion, "sample.bytes")!));
        Assert.Equal(new byte[] { 9, 8, 7, 6 },
            File.ReadAllBytes(release.FindMaster(ResourceReleaseFixture.MasterVersion, "overlay.bytes")!));
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, File.ReadAllBytes(release.FindAndroid(fixture.Baseline.CatalogName)!));
        Assert.Equal(File.ReadAllBytes(release.FindAndroid(fixture.Baseline.CatalogName)!),
            File.ReadAllBytes(release.FindAndroid(fixture.Baseline.CatalogName + ".bin")!));
        Assert.Throws<IOException>(() => fixture.Build());
    }

    [Fact]
    public void EmptyPackageFileListCannotPublishARelease()
    {
        using var fixture = new SyntheticSongPackageFixture();
        fixture.Files.Clear();

        Assert.Throws<InvalidDataException>(() => fixture.Build());

        fixture.AssertOutputAbsent();
    }

    [Theory]
    [InlineData("catalog/catalog.bin")]
    [InlineData("catalog/catalog.hash")]
    [InlineData("master/MasterManifest.json")]
    [InlineData("master/overlay.bytes")]
    [InlineData("bundles/new.bundle")]
    public void EveryConsumedPackageFileMustBeDeclared(string path)
    {
        using var fixture = new SyntheticSongPackageFixture();
        fixture.Files.Remove(fixture.Entry(path));

        Assert.Throws<InvalidDataException>(() => fixture.Build());

        fixture.AssertOutputAbsent();
    }

    [Theory]
    [InlineData("catalog/catalog.bin")]
    [InlineData("catalog/catalog.hash")]
    [InlineData("master/MasterManifest.json")]
    public void RequiredPackageFilesCannotBeAbsent(string path)
    {
        using var fixture = new SyntheticSongPackageFixture();
        fixture.Files.Remove(fixture.Entry(path));
        File.Delete(fixture.PackageFile(path));

        Assert.Throws<InvalidDataException>(() => fixture.Build());

        fixture.AssertOutputAbsent();
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("package")]
    [InlineData("masterJson")]
    public void OutputCannotBeInsideAnInputDirectory(string input)
    {
        using var fixture = new SyntheticSongPackageFixture();
        var root = input switch
        {
            "baseline" => fixture.Baseline.Root,
            "package" => fixture.Package,
            _ => fixture.MasterJson
        };
        var output = Path.Combine(root, "new-release");

        Assert.Throws<InvalidDataException>(() => fixture.Build(output));

        fixture.AssertOutputAbsent(output);
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("package")]
    [InlineData("masterJson")]
    public void OutputCannotReuseAnInputDirectory(string input)
    {
        using var fixture = new SyntheticSongPackageFixture();
        var output = input switch
        {
            "baseline" => fixture.Baseline.Root,
            "package" => fixture.Package,
            _ => fixture.MasterJson
        };
        var baselineBytes = File.ReadAllBytes(fixture.Baseline.FilePath(fixture.Baseline.MasterFilePath));

        Assert.Throws<IOException>(() => fixture.Build(output));

        Assert.Equal(baselineBytes, File.ReadAllBytes(fixture.Baseline.FilePath(fixture.Baseline.MasterFilePath)));
        Assert.False(File.Exists(Path.Combine(output, "release.json")));
    }

    [Theory]
    [InlineData("catalog/catalog.bin")]
    [InlineData("master/overlay.bytes")]
    [InlineData("bundles/new.bundle")]
    public void SameLengthPackageChangesFailHashVerification(string path)
    {
        using var fixture = new SyntheticSongPackageFixture();
        var bytes = File.ReadAllBytes(fixture.PackageFile(path));
        var declaredSize = fixture.Entry(path)["size"]!.GetValue<long>();
        bytes[0] ^= 0xFF;
        fixture.WritePackageBytes(path, bytes, refreshEntry: false);
        Assert.Equal(declaredSize, new FileInfo(fixture.PackageFile(path)).Length);

        Assert.Throws<InvalidDataException>(() => fixture.Build());

        fixture.AssertOutputAbsent();
    }

    [Fact]
    public void DuplicatePackagePathsCannotPublishARelease()
    {
        using var fixture = new SyntheticSongPackageFixture();
        fixture.Files.Add(fixture.Entry("bundles/new.bundle").DeepClone());

        Assert.Throws<InvalidDataException>(() => fixture.Build());

        fixture.AssertOutputAbsent();
    }

    [Fact]
    public void MissingScoreReferencesFailBeforeOutputIsPublished()
    {
        using var fixture = new SyntheticSongPackageFixture();
        fixture.Baseline.WriteTable("MasterLiveMusic", new { _id = 11, _defaultUnlock = true, _easyID = 91 });
        fixture.Baseline.WriteTable("MasterLiveMusicScore", new { _id = 71 });

        Assert.Throws<InvalidDataException>(() => fixture.Build());

        fixture.AssertOutputAbsent();
    }

    [Fact]
    public void BundleReferencesMustHaveDeclaredPackageFiles()
    {
        using var fixture = new SyntheticSongPackageFixture();
        fixture.Files.Remove(fixture.Entry("bundles/new.bundle"));
        File.Delete(fixture.PackageFile("bundles/new.bundle"));

        Assert.Throws<InvalidDataException>(() => fixture.Build());

        fixture.AssertOutputAbsent();
    }

    [Fact]
    public void BundleFilenamesMustBeUniqueInTheOutput()
    {
        using var fixture = new SyntheticSongPackageFixture();
        fixture.AddBundle("other/new.bundle", [1, 2, 3]);

        Assert.Throws<IOException>(() => fixture.Build());

        fixture.AssertOutputAbsent();
    }

    [Fact]
    public void DuplicateBundleReferencesCannotPublishARelease()
    {
        using var fixture = new SyntheticSongPackageFixture();
        fixture.Bundles.Add(fixture.Bundles[0]!.DeepClone());

        Assert.Throws<IOException>(() => fixture.Build());

        fixture.AssertOutputAbsent();
    }

    [Fact]
    public void BundleFilenamesCannotCollideWithTheCatalog()
    {
        using var fixture = new SyntheticSongPackageFixture();
        fixture.AddBundle($"bundles/{fixture.Baseline.CatalogName}.bin", [1, 2, 3]);

        Assert.Throws<IOException>(() => fixture.Build());

        fixture.AssertOutputAbsent();
    }

    [Fact]
    public void BundleUrlMustEndWithTheBundleFilename()
    {
        using var fixture = new SyntheticSongPackageFixture();
        fixture.Bundles[0]!["internal_id"] = "https://example.invalid/asset/Android/other.bundle";

        Assert.Throws<InvalidDataException>(() => fixture.Build());

        fixture.AssertOutputAbsent();
    }

    private sealed class SyntheticSongPackageFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mynotes-tests", Guid.NewGuid().ToString("N"));
        public ResourceReleaseFixture Baseline { get; } = new(includeMasterJson: true);
        public string Package => Path.Combine(_root, "package");
        public string MasterJson => Baseline.FilePath("tables");
        public string Output => Path.Combine(_root, "release");
        public JsonObject Manifest { get; }
        public JsonArray Files => Manifest["files"]!.AsArray();
        public JsonArray Bundles => Manifest["bundles"]!.AsArray();

        public SyntheticSongPackageFixture()
        {
            Directory.CreateDirectory(Package);
            var baselineOverlay = $"master/{ResourceReleaseFixture.MasterVersion}/overlay.bytes";
            Baseline.WriteBytes(baselineOverlay, [1, 1, 1, 1]);
            Baseline.MasterManifest["files"]!.AsArray().Add(MasterEntry("overlay.bytes", Baseline.Entry(baselineOverlay)));
            Baseline.SaveMasterManifest();
            Manifest = new JsonObject
            {
                ["format"] = "nnnotes.song-build/1",
                ["version"] = ResourceReleaseFixture.MasterVersion,
                ["baseline"] = new JsonObject
                {
                    ["catalogSha256"] = Baseline.Entry(Baseline.CatalogPath)["sha256"]!.GetValue<string>()
                },
                ["bundles"] = new JsonArray(),
                ["files"] = new JsonArray()
            };
            WritePackageBytes("catalog/catalog.bin", [4, 3, 2, 1]);
            WritePackageText("catalog/catalog.hash", Entry("catalog/catalog.bin")["sha256"]!.GetValue<string>()[..32]);
            WritePackageBytes("master/overlay.bytes", [9, 8, 7, 6]);
            var masterManifest = new JsonObject
            {
                ["version"] = ResourceReleaseFixture.MasterVersion,
                ["files"] = new JsonArray(
                    MasterEntry("sample.bytes", Baseline.Entry(Baseline.MasterFilePath)),
                    MasterEntry("overlay.bytes", Entry("master/overlay.bytes")))
            };
            WritePackageText("master/MasterManifest.json", masterManifest.ToJsonString());
            AddBundle("bundles/new.bundle", [9, 8, 7]);
        }

        public string PackageFile(string relative) =>
            Path.Combine(Package, relative.Replace('/', Path.DirectorySeparatorChar));

        public JsonObject Entry(string relative) =>
            Files.Single(entry => entry!["path"]!.GetValue<string>() == relative)!.AsObject();

        public void WritePackageBytes(string relative, byte[] bytes, bool refreshEntry = true)
        {
            var path = PackageFile(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            if (!refreshEntry)
                return;
            var previous = Files.SingleOrDefault(entry => entry!["path"]!.GetValue<string>() == relative);
            if (previous != null)
                Files.Remove(previous);
            Files.Add(new JsonObject
            {
                ["path"] = relative,
                ["size"] = (long)bytes.Length,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            });
        }

        private void WritePackageText(string relative, string text) =>
            WritePackageBytes(relative, Encoding.UTF8.GetBytes(text));

        public void AddBundle(string relative, byte[] bytes)
        {
            WritePackageBytes(relative, bytes);
            Bundles.Add(new JsonObject
            {
                ["path"] = relative,
                ["internal_id"] = "https://example.invalid/asset/Android/" + Path.GetFileName(PackageFile(relative))
            });
        }

        public string Build(string? output = null)
        {
            File.WriteAllText(PackageFile("package.json"), Manifest.ToJsonString());
            return ResourceReleaseBuilder.Build(Baseline.Root, Package, MasterJson, Baseline.CatalogName,
                ResourceReleaseFixture.ResourceVersion, output ?? Output);
        }

        public void AssertOutputAbsent(string? output = null)
        {
            var directory = output ?? Output;
            Assert.False(Directory.Exists(directory));
            Assert.False(File.Exists(directory));
            Assert.False(File.Exists(Path.Combine(directory, "release.json")));
        }

        public void Dispose()
        {
            Baseline.Dispose();
            Directory.Delete(_root, recursive: true);
        }

        private static JsonObject MasterEntry(string name, JsonObject file) => new()
        {
            ["name"] = name,
            ["size"] = file["size"]!.GetValue<long>(),
            ["hash"] = file["sha256"]!.GetValue<string>()
        };
    }

    private sealed class BuilderEnvironment(string root) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "MyNotes";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
