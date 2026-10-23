using System.Security.Cryptography;
using System.Text;
using App.Protobuf.Entity;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MyNotes.Config;
using MyNotes.Middleware;
using MyNotes.Services;
using Xunit;

namespace MyNotes.Tests;

public sealed class PlayerLoginTests
{
    private const string FirstUid = "test-account-one";
    private const string FirstToken = "test-token-one";
    private const string SecondUid = "test-account-two";
    private const string SecondToken = "test-token-two";
    private readonly PlayerManager _players = new(NullLogger<PlayerManager>.Instance);
    private readonly PlayerProtocolBuilder _protocol;

    public PlayerLoginTests()
    {
        var environment = new EmptyEnvironment();
        ServerConfig.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Game:DataDirectory"] = "empty", ["Game:RequireBasicAuth"] = "false" }).Build());
        var master = new MasterDataService(environment, NullLogger<MasterDataService>.Instance, new ResourceReleaseService(environment));
        _protocol = new PlayerProtocolBuilder(master);
    }

    public static IEnumerable<object[]> MalformedPayloads()
    {
        yield return [new byte[] { 0 }];
        yield return [new byte[] { 10, 2, 97 }];
        yield return [new byte[] { 18, 128 }];
    }

    [Theory]
    [MemberData(nameof(MalformedPayloads))]
    public void MalformedProtobufIsRejected(byte[] payload)
    {
        var service = CreateService();

        Assert.Throws<InvalidProtocolBufferException>(() => service.PreLogin(payload));
        Assert.Throws<InvalidProtocolBufferException>(() => service.Login(payload));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(FirstUid, null)]
    [InlineData(null, FirstToken)]
    [InlineData(FirstUid, "")]
    [InlineData("unknown-account", FirstToken)]
    [InlineData(FirstUid, "wrong-token")]
    [InlineData(FirstUid, SecondToken)]
    public void InvalidCredentialsReturnUnauthenticated(string? uid, string? token)
    {
        var service = CreateService();
        var payload = LoginPayload(uid, token);

        Assert.Equal("16", Assert.Throws<GrpcStatusException>(() => service.PreLogin(payload)).Status);
        Assert.Equal("16", Assert.Throws<GrpcStatusException>(() => service.Login(payload)).Status);
    }

    [Fact]
    public void ConfiguredTokenDigestAuthenticatesAndPreLoginReturnsTrue()
    {
        var service = CreateService();
        var payload = LoginPayload(FirstUid, FirstToken);
        using var input = new CodedInputStream(service.PreLogin(payload));

        Assert.Equal(8u, input.ReadTag());
        Assert.True(input.ReadBool());
        Assert.Equal(0u, input.ReadTag());

        var (credential, _, _) = ReadLogin(service.Login(payload));
        Assert.False(string.IsNullOrEmpty(credential.Id));
        Assert.False(string.IsNullOrEmpty(credential.Credential));
        Assert.False(string.IsNullOrEmpty(credential.DeviceId));
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Bearer " + credential.Credential;
        Assert.Equal(credential.Id, _players.GetFromRequest(request).PlayerId);
    }

    [Fact]
    public void RepeatedLoginReturnsTheSamePlayerCredential()
    {
        var service = CreateService();
        var payload = LoginPayload(FirstUid, FirstToken);

        var (first, _, _) = ReadLogin(service.Login(payload));
        var (second, _, _) = ReadLogin(service.Login(payload));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Credential, second.Credential);
        Assert.Equal(first.DeviceId, second.DeviceId);
    }

    [Fact]
    public void DistinctAccountsHaveDistinctPlayersAndCredentials()
    {
        var service = CreateService();

        var (first, _, _) = ReadLogin(service.Login(LoginPayload(FirstUid, FirstToken)));
        var (second, _, _) = ReadLogin(service.Login(LoginPayload(SecondUid, SecondToken)));

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Credential, second.Credential);
        Assert.NotEqual(first.DeviceId, second.DeviceId);
    }

    [Fact]
    public void ServerListContainsConfiguredWireFields()
    {
        var values = new[] { "TestServer", "https://cdn.example.test/", "https://api.example.test/", "https://chat.example.test/",
            "https://at.example.test/", "https://live.example.test/", "Test Display", "test-area", "test-age-icon" };
        var keys = new[] { "Name", "CdnRoot", "ApiServerRoot", "ChatServerRoot", "AtServerRoot", "LiveServer", "DisplayName", "AreaId", "AgeIconSpriteName" };
        var settings = AccountSettings();
        for (var index = 0; index < keys.Length; index++)
            settings["Login:Server:" + keys[index]] = values[index];
        var service = CreateService(settings);
        using var response = new CodedInputStream(service.ServerList());
        Assert.Equal(10u, response.ReadTag());
        using var server = new CodedInputStream(response.ReadBytes().ToByteArray());
        for (var index = 0; index < values.Length; index++)
        {
            Assert.Equal((uint)((index + 1) << 3 | 2), server.ReadTag());
            Assert.Equal(values[index], server.ReadString());
        }
        Assert.Equal(0u, server.ReadTag());
        Assert.Equal(0u, response.ReadTag());

        var (_, name, displayName) = ReadLogin(service.Login(LoginPayload(FirstUid, FirstToken)));
        Assert.Equal(values[0], name);
        Assert.Equal(values[6], displayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void InvalidTokenDigestsAreRejectedAtConstruction(string digest)
    {
        var settings = AccountSettings();
        settings["Login:Accounts:0:AccessTokenSha256"] = digest;

        Assert.Throws<InvalidOperationException>(() => CreateService(settings));
    }

    [Fact]
    public void DuplicateAccountUidsAreRejectedAtConstruction()
    {
        var settings = AccountSettings();
        settings["Login:Accounts:1:Uid"] = FirstUid;

        Assert.Throws<InvalidOperationException>(() => CreateService(settings));
    }

    private PlayerLoginService CreateService(Dictionary<string, string?>? settings = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings ?? AccountSettings()).Build(), _players, _protocol);

    private static Dictionary<string, string?> AccountSettings() => new()
    {
        ["Login:Accounts:0:Uid"] = FirstUid,
        ["Login:Accounts:0:AccessTokenSha256"] = TokenDigest(FirstToken),
        ["Login:Accounts:0:InitialDataGroup"] = "Default",
        ["Login:Accounts:1:Uid"] = SecondUid,
        ["Login:Accounts:1:AccessTokenSha256"] = TokenDigest(SecondToken),
        ["Login:Accounts:1:InitialDataGroup"] = "Default"
    };

    private static string TokenDigest(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static byte[] LoginPayload(string? uid, string? token)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            if (uid != null) { output.WriteTag(1, WireFormat.WireType.LengthDelimited); output.WriteString(uid); }
            if (token != null) { output.WriteTag(2, WireFormat.WireType.LengthDelimited); output.WriteString(token); }
        }
        return stream.ToArray();
    }

    private static (PlayerCredential Credential, string Name, string DisplayName) ReadLogin(byte[] payload)
    {
        using var input = new CodedInputStream(payload);
        Assert.Equal(10u, input.ReadTag());
        var credential = PlayerCredential.Parser.ParseFrom(input.ReadBytes());
        Assert.Equal(18u, input.ReadTag());
        var name = input.ReadString();
        Assert.Equal(26u, input.ReadTag());
        var displayName = input.ReadString();
        Assert.Equal(0u, input.ReadTag());
        return (credential, name, displayName);
    }

    private sealed class EmptyEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "MyNotes";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
