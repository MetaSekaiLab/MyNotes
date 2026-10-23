using App.Protobuf.Entity;
using App.Protobuf.Live;
using MyNotes.Middleware;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class FreeLiveService(MasterDataService master)
{
    public StartFreeResponse Start(PlayerRecord player, StartFreeRequest request)
    {
        if (!master.LiveScoreSeeds.Contains(new LiveScoreSeed(request.MusicId, request.Difficulty)))
            throw new GrpcStatusException("3", "unknown song or difficulty");
        if (request.Boost is < 0 or > 10)
            throw new GrpcStatusException("3", "boost must be between zero and ten");
        var initial = master.GetInitialPlayerData(player.InitialDataGroup);
        lock (player.DeckStateLock)
        {
            if (!player.DeckOverrides.ContainsKey(request.DeckId) && !initial.Decks.Any(deck => deck.Id == request.DeckId))
                throw new GrpcStatusException("3", "unknown deck");
        }
        lock (player.LiveStateLock)
            player.ActiveFreeLive = (request.MusicId, request.Difficulty);
        return new StartFreeResponse();
    }

    public FinishFreeResponse Finish(PlayerRecord player, FinishFreeRequest request)
    {
        var finish = request.LiveFinish;
        var score = finish?.SoloScore ?? request.Score;
        var combo = finish?.Combo ?? request.Combo;
        var status = finish?.ClearStatus ?? request.ClearStatus;
        if (score < 0 || combo < 0 || status is < 1 or > 5 ||
            finish?.JudgementCounts.Any(count => count.TotalCount < 0) == true)
            throw new GrpcStatusException("3", "invalid live result");
        if (finish != null && (request.Score != 0 && request.Score != score ||
            request.Combo != 0 && request.Combo != combo || request.ClearStatus != 0 && request.ClearStatus != status))
            throw new GrpcStatusException("3", "live result fields disagree");

        lock (player.LiveStateLock)
        {
            if (player.ActiveFreeLive is not { } active)
                throw new GrpcStatusException("9", "no active free live");
            if (!player.LiveScores.TryGetValue(active, out var saved))
            {
                saved = new LiveScore { MusicId = active.MusicId, Difficulty = active.Difficulty, Mode = 1 };
                player.LiveScores.Add(active, saved);
            }
            saved.HighScore = Math.Max(saved.HighScore, score);
            saved.MaxCombo = Math.Max(saved.MaxCombo, combo);
            saved.ClearStatus = Math.Max(saved.ClearStatus, status);
            player.ActiveFreeLive = null;
        }
        return new FinishFreeResponse { Notification = new Notification() };
    }
}
