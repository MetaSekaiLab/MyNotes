using App.Protobuf.Home;
using App.Protobuf.Player;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyNotes.Config;
using MyNotes.Models;
using MyNotes.Services;
using Xunit;

namespace MyNotes.Tests;

public sealed class ClientStateTests
{
    [Fact]
    public void PlayerDataContainsInitializedClientStateAndStableAccountId()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        ServerConfig.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Game:RequireBasicAuth"] = "false" }).Build());
        var release = fixture.CreateRelease();
        var master = new MasterDataService(fixture.Environment, NullLogger<MasterDataService>.Instance, release);
        var players = new PlayerManager(NullLogger<PlayerManager>.Instance);
        var player = players.Register("Default");
        var protocol = new PlayerProtocolBuilder(master);
        var response = protocol.GetPlayerData(player, players);

        foreach (var candidate in new[] { response, response.Clone(), GetPlayerDataResponse.Parser.ParseFrom(response.ToByteArray()) })
        {
            var fields = ReadFields(candidate.PlayerData!);
            foreach (var field in new[] { 49, 50, 53, 60 })
                Assert.Equal(ByteString.Empty, fields[field]);
            Assert.Equal(player.ProfileId, ReadFields(candidate)[6]);
            Assert.NotNull(candidate.PlayerData!.MyProfile);
            Assert.Equal(player.ProfileId, candidate.PlayerData.MyProfile.ProfileId);
        }
    }

    [Fact]
    public void HomeContainsInitializedPersonalNotifications()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        var master = new MasterDataService(fixture.Environment, NullLogger<MasterDataService>.Instance, fixture.CreateRelease());
        var players = new PlayerManager(NullLogger<PlayerManager>.Instance);
        var player = players.Register("Default");
        var protocol = new HomeProtocolBuilder(players, new PlayerProtocolBuilder(master));
        var response = protocol.Get(new HomeSnapshot { HasNotification = true, HasFriends = true }, player);

        foreach (var candidate in new[] { response, response.Clone(), GetHomeResponse.Parser.ParseFrom(response.ToByteArray()) })
        {
            Assert.Equal(ByteString.Empty, ReadFields(candidate)[10]);
            Assert.NotNull(candidate.Notification);
            Assert.NotNull(candidate.Friends);
        }
    }

    private static Dictionary<int, object> ReadFields(IMessage message)
    {
        using var input = new CodedInputStream(message.ToByteArray());
        var fields = new Dictionary<int, object>();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var number = WireFormat.GetTagFieldNumber(tag);
            switch (WireFormat.GetTagWireType(tag))
            {
                case WireFormat.WireType.LengthDelimited:
                    fields[number] = input.ReadBytes();
                    break;
                case WireFormat.WireType.Varint:
                    fields[number] = input.ReadInt64();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return fields;
    }
}
