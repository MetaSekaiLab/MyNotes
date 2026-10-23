using App.Protobuf.Masterdata;
using Google.Protobuf;
using MyNotes.Services;
using Xunit;

namespace MyNotes.Tests;

public sealed class MasterDataTests
{
    [Fact]
    public void ScoreSeedsFollowEachSongsExplicitDifficultyReferences()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        fixture.WriteTable("MasterLiveMusic",
            new { _id = 17, _easyID = 0, _normalID = 84, _hardID = 0, _expertID = 121 },
            new { _id = 11, _defaultUnlock = true, _easyID = 71, _normalID = 83, _hardID = 94, _expertID = 120 },
            new { _id = 23, _defaultUnlock = false, _easyID = 333, _normalID = 0, _hardID = 0, _expertID = 0 });
        fixture.WriteTable("MasterLiveMusicScore",
            new { _id = 120 }, new { _id = 94 }, new { _id = 71 }, new { _id = 83 },
            new { _id = 121 }, new { _id = 84 }, new { _id = 199 });

        var master = fixture.CreateMasterData();

        Assert.Equal(new long[] { 11, 17 }, master.DefaultUnlockedLiveMusicIds);
        Assert.Equal(new[]
        {
            new LiveScoreSeed(11, 1), new LiveScoreSeed(11, 2), new LiveScoreSeed(11, 3),
            new LiveScoreSeed(11, 4), new LiveScoreSeed(17, 2), new LiveScoreSeed(17, 4)
        }, master.LiveScoreSeeds);
    }

    [Theory]
    [InlineData("_easyID")]
    [InlineData("_normalID")]
    [InlineData("_hardID")]
    [InlineData("_expertID")]
    public void UnlockedSongsMustReferenceExistingScores(string field)
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        fixture.WriteTable("MasterLiveMusic", new Dictionary<string, object>
        {
            ["_id"] = 11, ["_defaultUnlock"] = true, [field] = 91
        });
        fixture.WriteTable("MasterLiveMusicScore", new { _id = 71 });

        Assert.Throws<InvalidDataException>(() => fixture.CreateMasterData());
    }

    [Fact]
    public void EmptyConfiguredTablesProduceEmptyCollections()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);

        var master = fixture.CreateMasterData();
        var initial = master.GetInitialPlayerData("Default");

        Assert.Empty(master.DefaultUnlockedLiveMusicIds);
        Assert.Empty(master.LiveScoreSeeds);
        Assert.Empty(master.LiveComboRewardDifficulties);
        Assert.Empty(initial.MemberCards);
        Assert.Empty(initial.SupportCards);
        Assert.Empty(initial.Decks);
    }

    [Fact]
    public void ConfiguredSongsCanHaveNoUnlockedEntries()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        fixture.WriteTable("MasterLiveMusic", new { _id = 11, _defaultUnlock = false, _easyID = 71 });
        fixture.WriteTable("MasterLiveMusicScore", new { _id = 71 });

        var master = fixture.CreateMasterData();

        Assert.Empty(master.DefaultUnlockedLiveMusicIds);
        Assert.Empty(master.LiveScoreSeeds);
    }

    [Fact]
    public void ComboRewardDifficultiesAreDistinctSortedServerValues()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        fixture.WriteTable("MasterLiveMusicComboReward",
            new { _id = 1, _difficulty = 3 }, new { _id = 2, _difficulty = 0 },
            new { _id = 3, _difficulty = 2 }, new { _id = 4, _difficulty = 1 },
            new { _id = 5, _difficulty = 2 }, new { _id = 6, _difficulty = -1 },
            new { _id = 7, _difficulty = 4 });

        var master = fixture.CreateMasterData();

        Assert.Equal(new[] { 1, 2, 3, 4 }, master.LiveComboRewardDifficulties);
    }

    [Fact]
    public void InitialCardsUseConfiguredRowsAndValidMasterReferences()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        fixture.WriteTable("MasterMemberCard", new { _id = 7 }, new { _id = 9 });
        fixture.WriteTable("MasterSupportCard", new { _id = 8 });
        fixture.WriteTable("MasterPlayerData",
            new
            {
                _id = 1, _group = "Default", _playerDataType = 1, _parameter1 = "7", _parameter2 = "12",
                _parameter3 = "2", _parameter4 = "3", _parameter5 = "4", _parameter6 = "5",
                _parameter7 = "6", _parameter8 = "7"
            },
            new { _id = 2, _group = "Default", _playerDataType = 1, _parameter1 = "99" },
            new
            {
                _id = 3, _group = "Default", _playerDataType = 2, _parameter1 = "8", _parameter2 = "13",
                _parameter3 = "4", _parameter4 = "2"
            },
            new { _id = 4, _group = "Default", _playerDataType = 2, _parameter1 = "98" },
            new
            {
                _id = 5, _group = "Default", _playerDataType = 5, _parameter0 = "1", _parameter1 = "Practice",
                _parameter2 = "7, 0, invalid", _parameter3 = "8"
            },
            new { _id = 6, _group = "CBT", _playerDataType = 1, _parameter1 = "9" });

        var master = fixture.CreateMasterData();
        var initial = master.GetInitialPlayerData("Default");

        Assert.Equal(new InitialMemberCardSeed(7, 12, 2, 3, 4, 5, 6, 7, 1), Assert.Single(initial.MemberCards));
        Assert.Equal(new InitialSupportCardSeed(8, 13, 4, 2), Assert.Single(initial.SupportCards));
        var deck = Assert.Single(initial.Decks);
        Assert.Equal(1, deck.Id);
        Assert.Equal("Practice", deck.Name);
        Assert.Equal(new long[] { 7 }, deck.MemberCardIds);
        Assert.Equal(new long[] { 8 }, deck.SupportCardIds);
        Assert.Equal(9, Assert.Single(master.GetInitialPlayerData("cbt").MemberCards).MasterId);
    }

    [Fact]
    public void EmptyConfiguredCardMastersProduceNoInitialCards()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        fixture.WriteTable("MasterPlayerData",
            new { _id = 1, _group = "Default", _playerDataType = 1, _parameter1 = "7" },
            new { _id = 2, _group = "Default", _playerDataType = 2, _parameter1 = "8" });

        var initial = fixture.CreateMasterData().GetInitialPlayerData("Default");

        Assert.Empty(initial.MemberCards);
        Assert.Empty(initial.SupportCards);
        Assert.Empty(initial.Decks);
    }

    [Fact]
    public void VersionResponseUsesTheVerifiedReleaseVersions()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);

        var master = fixture.CreateMasterData();
        var response = new MasterdataProtocolBuilder(master).Version();
        var decoded = VersionResponse.Parser.ParseFrom(response.ToByteArray());

        Assert.Equal(ResourceReleaseFixture.MasterVersion, master.Version);
        Assert.Equal(ResourceReleaseFixture.ResourceVersion, master.ResourceVersion);
        Assert.Equal(ResourceReleaseFixture.MasterVersion, decoded.Version);
        Assert.Equal(ResourceReleaseFixture.ResourceVersion, decoded.ResourceVersion);
    }
}
