using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using App.Protobuf.Masterdata;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MyNotes.Config;
using MyNotes.Services;
using Xunit;

namespace MyNotes.Tests;

public sealed class ResourceReleaseTests
{
    [Fact]
    public void VerifiedReleaseExposesItsVersionsAndRegisteredFiles()
    {
        using var fixture = new ResourceReleaseFixture();

        var release = fixture.CreateRelease();

        Assert.True(release.Enabled);
        Assert.Equal(ResourceReleaseFixture.MasterVersion, release.MasterVersion);
        Assert.Equal(ResourceReleaseFixture.ResourceVersion, release.ResourceVersion);
        Assert.Equal(fixture.CatalogName, release.CatalogName);
        Assert.Null(release.MasterJsonDirectory);
        Assert.Equal(fixture.FilePath(fixture.CatalogPath), release.FindAndroid(fixture.CatalogName));
        Assert.Equal(fixture.FilePath(fixture.CatalogPath), release.FindAndroid(fixture.CatalogName + ".bin"));
        Assert.Equal(fixture.FilePath(fixture.MarkerPath), release.FindAndroid(fixture.CatalogName + ".hash"));
        Assert.Equal(fixture.FilePath("Android/sample.bundle"), release.FindAndroid("sample.bundle"));
        Assert.Equal(fixture.FilePath(fixture.MasterManifestPath),
            release.FindMaster(ResourceReleaseFixture.MasterVersion, "MasterManifest.json"));
        Assert.Equal(fixture.FilePath(fixture.MasterFilePath),
            release.FindMaster(ResourceReleaseFixture.MasterVersion, "sample.bytes"));
        Assert.Null(release.FindMaster("other-version", "sample.bytes"));
        Assert.Null(release.FindMaster(ResourceReleaseFixture.MasterVersion, "missing.bytes"));
        Assert.Null(release.FindAndroid("missing.bundle"));
    }

    [Fact]
    public void CatalogNamesCanDifferForTheSameVersionsAndContent()
    {
        using var first = new ResourceReleaseFixture(catalogName: "catalog_one");
        using var second = new ResourceReleaseFixture(catalogName: "catalog_two");

        var firstRelease = first.CreateRelease();
        var secondRelease = second.CreateRelease();

        Assert.Equal(firstRelease.MasterVersion, secondRelease.MasterVersion);
        Assert.Equal(firstRelease.ResourceVersion, secondRelease.ResourceVersion);
        Assert.NotEqual(firstRelease.CatalogName, secondRelease.CatalogName);
        Assert.Equal(File.ReadAllBytes(firstRelease.FindAndroid(first.CatalogName)!),
            File.ReadAllBytes(secondRelease.FindAndroid(second.CatalogName)!));
        Assert.Equal(File.ReadAllText(firstRelease.FindAndroid(first.CatalogName + ".hash")!),
            File.ReadAllText(secondRelease.FindAndroid(second.CatalogName + ".hash")!));
    }

    [Fact]
    public void ConfiguredJsonDirectoryContainsVerifiedTables()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);

        var release = fixture.CreateRelease();

        Assert.Equal(fixture.FilePath("tables"), release.MasterJsonDirectory);
        Assert.All(ResourceReleaseFixture.TableNames, name =>
        {
            using var table = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(release.MasterJsonDirectory!, name + ".json")));
            Assert.Equal(JsonValueKind.Array, table.RootElement.GetProperty("_allData").ValueKind);
        });
    }

    [Fact]
    public void ModifiedFileContentFailsVerification()
    {
        using var fixture = new ResourceReleaseFixture();
        var bytes = File.ReadAllBytes(fixture.FilePath(fixture.CatalogPath));
        bytes[0] ^= 0xFF;
        fixture.WriteBytes(fixture.CatalogPath, bytes, refreshEntry: false);

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Theory]
    [InlineData("size")]
    [InlineData("sha256")]
    public void IncorrectFileMetadataFailsVerification(string property)
    {
        using var fixture = new ResourceReleaseFixture();
        var entry = fixture.Entry(fixture.CatalogPath);
        if (property == "size")
            entry[property] = entry[property]!.GetValue<long>() + 1;
        else
            entry[property] = new string('0', 64);

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Fact]
    public void CatalogMarkerMustMatchTheCatalogHashPrefix()
    {
        using var fixture = new ResourceReleaseFixture();
        fixture.WriteText(fixture.MarkerPath, new string('0', 32));

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Theory]
    [InlineData("name")]
    [InlineData("size")]
    [InlineData("hash")]
    public void MasterReferencesMustMatchRegisteredFiles(string property)
    {
        using var fixture = new ResourceReleaseFixture();
        var entry = fixture.MasterManifest["files"]![0]!.AsObject();
        if (property == "name")
            entry[property] = "missing.bytes";
        else if (property == "size")
            entry[property] = entry[property]!.GetValue<long>() + 1;
        else
            entry[property] = new string('0', 64);
        fixture.SaveMasterManifest();

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Fact]
    public void MasterManifestVersionMustMatchTheRelease()
    {
        using var fixture = new ResourceReleaseFixture();
        fixture.MasterManifest["version"] = "other-version";
        fixture.SaveMasterManifest();

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Fact]
    public void DuplicateReleasePathsAreRejected()
    {
        using var fixture = new ResourceReleaseFixture();
        fixture.Files.Add(fixture.Entry(fixture.CatalogPath).DeepClone());

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Fact]
    public void DuplicateMasterNamesAreRejected()
    {
        using var fixture = new ResourceReleaseFixture();
        var files = fixture.MasterManifest["files"]!.AsArray();
        files.Add(files[0]!.DeepClone());
        fixture.SaveMasterManifest();

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("marker")]
    [InlineData("master")]
    public void RequiredFilesMustBeRegistered(string file)
    {
        using var fixture = new ResourceReleaseFixture();
        var path = file switch
        {
            "catalog" => fixture.CatalogPath,
            "marker" => fixture.MarkerPath,
            _ => fixture.MasterManifestPath
        };
        fixture.Files.Remove(fixture.Entry(path));

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("Android/../outside.bin")]
    [InlineData("/catalog.bin")]
    [InlineData("Android//catalog.bin")]
    [InlineData("Android\\catalog.bin")]
    [InlineData("C:/catalog.bin")]
    public void ReleasePathsMustContainSafeRelativeSegments(string path)
    {
        using var fixture = new ResourceReleaseFixture();
        fixture.Files.Add(new JsonObject { ["path"] = path, ["size"] = 0, ["sha256"] = new string('0', 64) });

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("resourceVersion")]
    [InlineData("catalogName")]
    public void ReleaseIdentitiesMustBeSafeSegments(string property)
    {
        using var fixture = new ResourceReleaseFixture();
        fixture.Manifest[property] = "../other";

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("nul.bin")]
    [InlineData("sample.")]
    [InlineData("sample:part")]
    public void UnsafeResourceSegmentsAreRejected(string segment)
    {
        Assert.False(ResourcePaths.IsSegment(segment));
    }

    [Fact]
    public void UnsupportedReleaseFormatIsRejected()
    {
        using var fixture = new ResourceReleaseFixture();
        fixture.Manifest["format"] = "mynotes.resource-release/2";

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Fact]
    public void JsonTablesMustContainAnArrayOfRows()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        fixture.WriteText("tables/MasterLiveMusic.json", "{\"_allData\":{}}");

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [Fact]
    public void JsonTablesMustBeRegisteredInTheRelease()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        fixture.Files.Remove(fixture.Entry("tables/MasterLiveMusicScore.json"));

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [SymbolicLinkFact]
    public void ReleaseFilesCannotBeSymbolicLinks()
    {
        using var fixture = new ResourceReleaseFixture();
        var source = fixture.FilePath(fixture.CatalogPath);
        var target = fixture.FilePath("catalog-target.bin");
        File.Move(source, target);
        File.CreateSymbolicLink(source, target);

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [SymbolicLinkFact]
    public void ReleaseDirectoriesCannotBeSymbolicLinks()
    {
        using var fixture = new ResourceReleaseFixture();
        var source = fixture.FilePath("Android");
        var target = fixture.FilePath("catalog-target");
        Directory.Move(source, target);
        Directory.CreateSymbolicLink(source, target);

        Assert.Throws<InvalidDataException>(() => fixture.CreateRelease());
    }

    [SymbolicLinkFact]
    public void ResourceRootCannotBeASymbolicLink()
    {
        using var fixture = new ResourceReleaseFixture();
        var link = fixture.FilePath("linked-root");
        Directory.CreateSymbolicLink(link, fixture.Root);
        try
        {
            Assert.Throws<InvalidDataException>(() => ResourcePaths.Resolve(link, "sample.bin"));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void VersionResponsePreservesBothVersionsOnTheWire()
    {
        var response = new VersionResponse { Version = "master-v1", ResourceVersion = "resources-v2" };

        var bytes = response.ToByteArray();
        var decoded = VersionResponse.Parser.ParseFrom(bytes);

        Assert.Equal(bytes.Length, response.CalculateSize());
        Assert.Equal(response, decoded);
        Assert.Equal(response, response.Clone());
        Assert.Equal(2, VersionResponse.ResourceVersionFieldNumber);
        using var input = new CodedInputStream(bytes);
        Assert.Equal(10u, input.ReadTag());
        Assert.Equal("master-v1", input.ReadString());
        Assert.Equal(18u, input.ReadTag());
        Assert.Equal("resources-v2", input.ReadString());
        Assert.Equal(0u, input.ReadTag());
    }

    [Fact]
    public void VersionResponseMergesNonemptyResourceVersion()
    {
        var response = new VersionResponse { Version = "master-v1", ResourceVersion = "resources-v1" };

        response.MergeFrom(new VersionResponse { ResourceVersion = "resources-v2" });
        response.MergeFrom(new VersionResponse());

        Assert.Equal("master-v1", response.Version);
        Assert.Equal("resources-v2", response.ResourceVersion);
    }
}

internal sealed class ResourceReleaseFixture : IDisposable
{
    public const string MasterVersion = "master-v1";
    public const string ResourceVersion = "resources-v2";
    public static readonly string[] TableNames =
    [
        "MasterPlayerData", "MasterMemberCard", "MasterSupportCard", "MasterLiveMusic",
        "MasterLiveMusicScore", "MasterLiveMusicComboReward"
    ];

    private readonly Dictionary<string, string?> _configuration = CaptureConfiguration();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mynotes-tests", Guid.NewGuid().ToString("N"));
    public string CatalogName { get; }
    public string CatalogPath => $"Android/{CatalogName}.bin";
    public string MarkerPath => $"Android/{CatalogName}.hash";
    public string MasterManifestPath => $"master/{MasterVersion}/MasterManifest.json";
    public string MasterFilePath => $"master/{MasterVersion}/sample.bytes";
    public JsonObject Manifest { get; }
    public JsonObject MasterManifest { get; }
    public JsonArray Files => Manifest["files"]!.AsArray();
    public IHostEnvironment Environment { get; }

    public ResourceReleaseFixture(bool includeMasterJson = false, string catalogName = "catalog_sample")
    {
        CatalogName = catalogName;
        Directory.CreateDirectory(Root);
        Environment = new TestHostEnvironment(Root);
        Manifest = new JsonObject
        {
            ["format"] = "mynotes.resource-release/1",
            ["version"] = MasterVersion,
            ["resourceVersion"] = ResourceVersion,
            ["catalogName"] = CatalogName,
            ["files"] = new JsonArray()
        };
        WriteBytes(MasterFilePath, [1, 4, 7, 10]);
        var masterEntry = Entry(MasterFilePath);
        MasterManifest = new JsonObject
        {
            ["version"] = MasterVersion,
            ["files"] = new JsonArray(new JsonObject
            {
                ["name"] = "sample.bytes",
                ["size"] = masterEntry["size"]!.GetValue<long>(),
                ["hash"] = masterEntry["sha256"]!.GetValue<string>()
            })
        };
        SaveMasterManifest();
        WriteBytes(CatalogPath, [2, 5, 8, 11, 14, 17]);
        WriteText(MarkerPath, Entry(CatalogPath)["sha256"]!.GetValue<string>()[..32]);
        WriteText("Android/sample.bundle", "sample bundle content");
        if (includeMasterJson)
        {
            Manifest["masterJsonDirectory"] = "tables";
            foreach (var name in TableNames)
                WriteTable(name, []);
        }
    }

    public string FilePath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public JsonObject Entry(string path) => Files.Single(entry => entry!["path"]!.GetValue<string>() == path)!.AsObject();

    public void WriteBytes(string relative, byte[] bytes, bool refreshEntry = true)
    {
        var path = FilePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        if (!refreshEntry)
            return;
        var existing = Files.SingleOrDefault(entry => entry!["path"]!.GetValue<string>() == relative);
        if (existing != null)
            Files.Remove(existing);
        Files.Add(new JsonObject
        {
            ["path"] = relative,
            ["size"] = (long)bytes.Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        });
    }

    public void WriteText(string relative, string text, bool refreshEntry = true) =>
        WriteBytes(relative, Encoding.UTF8.GetBytes(text), refreshEntry);

    public void WriteTable(string name, params object[] rows) =>
        WriteText($"tables/{name}.json", JsonSerializer.Serialize(new { _allData = rows }));

    public void SaveMasterManifest() => WriteText(MasterManifestPath, MasterManifest.ToJsonString());

    public ResourceReleaseService CreateRelease()
    {
        var settings = new Dictionary<string, string?>(_configuration)
        {
            ["Game:MasterVersion"] = "configured-master",
            ["Resources:ResourceVersion"] = "configured-resources",
            ["Resources:ReleaseManifest"] = "release.json"
        };
        ServerConfig.Load(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        File.WriteAllText(FilePath("release.json"), Manifest.ToJsonString());
        return new ResourceReleaseService(Environment);
    }

    public MasterDataService CreateMasterData() =>
        new(Environment, NullLogger<MasterDataService>.Instance, CreateRelease());

    public void Dispose()
    {
        ServerConfig.Load(new ConfigurationBuilder().AddInMemoryCollection(_configuration).Build());
        Directory.Delete(Root, recursive: true);
    }

    private static Dictionary<string, string?> CaptureConfiguration()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Server:GrpcPort"] = ServerConfig.GrpcPort.ToString(),
            ["Server:HttpPort"] = ServerConfig.HttpPort.ToString(),
            ["Game:MasterVersion"] = ServerConfig.MasterVersion,
            ["Game:InitialDataGroup"] = ServerConfig.InitialDataGroup,
            ["Game:BasicAuthUser"] = ServerConfig.BasicAuthUser,
            ["Game:BasicAuthPass"] = ServerConfig.BasicAuthPass,
            ["Game:RequireBasicAuth"] = ServerConfig.RequireBasicAuth.ToString(),
            ["Game:DataDirectory"] = ServerConfig.DataDirectory,
            ["Static:OfficialAssetRootUrl"] = ServerConfig.OfficialAssetRootUrl,
            ["Static:OfficialAssetRequestTimeoutSeconds"] = ServerConfig.OfficialAssetRequestTimeoutSeconds.ToString(),
            ["Static:CacheEnabled"] = ServerConfig.StaticAssetCacheEnabled.ToString(),
            ["Static:CacheDirectory"] = ServerConfig.StaticAssetCacheDirectory,
            ["Static:LocalDirectory"] = ServerConfig.StaticAssetLocalDirectory,
            ["Static:UpstreamEnabled"] = ServerConfig.StaticAssetUpstreamEnabled.ToString(),
            ["Resources:ReleaseManifest"] = ServerConfig.ResourceReleaseManifest,
            ["Resources:ResourceVersion"] = ServerConfig.ResourceVersion
        };
        for (var index = 0; index < ServerConfig.InitialStampIds.Count; index++)
            settings[$"Game:InitialStampIds:{index}"] = ServerConfig.InitialStampIds[index].ToString();
        return settings;
    }

    private sealed class TestHostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MyNotes.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
