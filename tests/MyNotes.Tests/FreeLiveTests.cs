using App.Protobuf.Entity;
using App.Protobuf.Live;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MyNotes.Config;
using MyNotes.Middleware;
using MyNotes.Models;
using MyNotes.Services;
using Xunit;

namespace MyNotes.Tests;

public sealed class FreeLiveTests
{
    private readonly MasterDataService _master;
    private readonly PlayerManager _players = new(NullLogger<PlayerManager>.Instance);
    private readonly FreeLiveService _live;
    private readonly PlayerRecord _player;

    public FreeLiveTests()
    {
        var environment = new EmptyEnvironment();
        ServerConfig.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Game:DataDirectory"] = "empty", ["Game:RequireBasicAuth"] = "false" }).Build());
        _master = new MasterDataService(environment, NullLogger<MasterDataService>.Instance, new ResourceReleaseService(environment));
        _live = new FreeLiveService(_master);
        _player = _players.Register("Default");
    }

    private StartFreeRequest StartRequest() => new()
    {
        MusicId = _master.LiveScoreSeeds[0].MusicId,
        Difficulty = _master.LiveScoreSeeds[0].Difficulty,
        DeckId = _master.GetInitialPlayerData("Default").Decks[0].Id
    };

    [Fact]
    public void CompletedResultsAreReturnedWithPlayerData()
    {
        var start = StartRequest();
        _live.Start(_player, start);
        var response = _live.Finish(_player, new FinishFreeRequest { Score = 300, Combo = 8, ClearStatus = 4 });
        Assert.NotNull(response.Notification);
        Assert.NotEmpty(response.ToByteArray());
        Assert.NotNull(FinishFreeResponse.Parser.ParseFrom(response.ToByteArray()).Notification);
        var scores = new PlayerProtocolBuilder(_master).GetPlayerData(_player, _players).PlayerData!.LiveScore;
        var saved = Assert.Single(scores, score => score.MusicId == start.MusicId && score.Difficulty == start.Difficulty);
        Assert.Equal(300, saved.HighScore);
        Assert.Equal(8, saved.MaxCombo);
        Assert.Equal(4, saved.ClearStatus);
        Assert.Equal(1, saved.Mode);
    }

    [Fact]
    public void BestScoreComboAndClearStateAreTrackedIndependently()
    {
        var start = StartRequest();
        _live.Start(_player, start);
        _live.Finish(_player, new FinishFreeRequest { Score = 500, Combo = 8, ClearStatus = 2 });
        _live.Start(_player, start);
        _live.Finish(_player, new FinishFreeRequest { LiveFinish = new LiveFinish { SoloScore = 200, Combo = 10, ClearStatus = 5 } });
        var saved = new PlayerProtocolBuilder(_master).GetPlayerData(_player, _players).PlayerData!.LiveScore
            .Single(score => score.MusicId == start.MusicId && score.Difficulty == start.Difficulty);
        Assert.Equal(500, saved.HighScore);
        Assert.Equal(10, saved.MaxCombo);
        Assert.Equal(5, saved.ClearStatus);
    }

    [Fact]
    public void FinishRequiresAStartAndCanBeConsumedOnce()
    {
        var request = new FinishFreeRequest { ClearStatus = 1 };
        Assert.Equal("9", Assert.Throws<GrpcStatusException>(() => _live.Finish(_player, request)).Status);
        _live.Start(_player, StartRequest());
        _live.Finish(_player, request);
        Assert.Equal("9", Assert.Throws<GrpcStatusException>(() => _live.Finish(_player, request)).Status);
    }

    [Fact]
    public void ResultsAreIsolatedBetweenPlayersAndDifficulties()
    {
        var start = StartRequest();
        _live.Start(_player, start);
        var other = _players.Register("Default");
        Assert.Equal("9", Assert.Throws<GrpcStatusException>(() => _live.Finish(other, new FinishFreeRequest { ClearStatus = 1 })).Status);
        _live.Finish(_player, new FinishFreeRequest { Score = 7, ClearStatus = 2 });
        Assert.All(new PlayerProtocolBuilder(_master).GetPlayerData(other, _players).PlayerData!.LiveScore,
            score => Assert.Equal(0, score.HighScore));
    }

    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(0, -1, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, 6)]
    public void InvalidResultsPreserveTheActiveLive(int score, int combo, int status)
    {
        _live.Start(_player, StartRequest());
        Assert.Equal("3", Assert.Throws<GrpcStatusException>(() => _live.Finish(_player,
            new FinishFreeRequest { Score = score, Combo = combo, ClearStatus = status })).Status);
        _live.Finish(_player, new FinishFreeRequest { ClearStatus = 1 });
    }

    [Fact]
    public void ConflictingNestedResultIsRejected()
    {
        _live.Start(_player, StartRequest());
        Assert.Equal("3", Assert.Throws<GrpcStatusException>(() => _live.Finish(_player,
            new FinishFreeRequest { Score = 5, LiveFinish = new LiveFinish { SoloScore = 6, ClearStatus = 2 } })).Status);
    }

    [Theory]
    [InlineData(0, 1, 1, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(1, 1, 0, 0)]
    public void UnknownSongDifficultyOrDeckIsRejected(long music, int difficulty, int deck, int boost)
    {
        Assert.Equal("3", Assert.Throws<GrpcStatusException>(() => _live.Start(_player,
            new StartFreeRequest { MusicId = music, Difficulty = difficulty, DeckId = deck, Boost = boost })).Status);
    }

    private sealed class EmptyEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "MyNotes";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
