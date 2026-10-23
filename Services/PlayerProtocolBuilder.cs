using App.Protobuf.Entity;
using App.Protobuf.Player;
using App.Protobuf.PlayerExt;
using Google.Protobuf;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class PlayerProtocolBuilder(MasterDataService master)
{
    private static readonly long[] InitialCharacterIds = [1, 2, 3, 4, 5];
    private const int LeaderSlotIndex = 2;
    private const int InitialPlayerRankExp = 1;
    private const int ProjectedDeckTotalPower = 1000;

    public RegisterResponse Register(PlayerRecord player) => new()
    {
        Credential = BuildCredential(player),
        ProfileId = player.ProfileId
    };

    public RegisterDeviceResponse RegisterDevice(PlayerRecord player) => new()
    {
        Credential = BuildCredential(player)
    };

    public RegisterDeviceForConnectedPlayerResponse RegisterDeviceForConnectedPlayer(PlayerRecord player) => new()
    {
        Credential = BuildCredential(player)
    };

    public GetPlayerDataResponse GetPlayerData(PlayerRecord player, PlayerManager players)
    {
        var invitationState = players.GetInvitationState(player);
        var response = new GetPlayerDataResponse
        {
            PlayerData = BuildPlayerData(player),
            Notification = new Notification(),
            Limitation = new Limitation(),
            Friends = BuildFriends(player, players),
            Invitation = new Invitation
            {
                IsInvitationCodeInputAlready = invitationState.InputAlready
            }
        };
        response.Invitation.NewInvitationPlayerIds.Add(invitationState.NewPlayerIds);
        ClientStateProtocol.SetAccountId(response, player.ProfileId);
        return response;
    }

    public SyncDataResponse SyncData(PlayerRecord player) => new()
    {
        Accountid = player.ProfileId,
        Gem = new Gem(),
        LiveBoost = new LiveBoost()
    };

    public PlayerSimpleProfile BuildSimpleProfile(PlayerRecord player)
    {
        var initialData = master.GetInitialPlayerData(player.InitialDataGroup);
        var (decks, mainDeckId) = BuildDeckState(player, initialData);
        var mainDeck = decks.FirstOrDefault(deck => deck.Id == mainDeckId);
        return BuildSimpleProfile(player, initialData, mainDeck);
    }

    public CirclePlayer BuildCirclePlayer(PlayerRecord player, ulong circleId, CircleAuth auth) => new()
    {
        PlayerId = player.PlayerId,
        CircleId = circleId,
        Auth = (uint)auth,
        Profile = BuildSimpleProfile(player)
    };

    public Friends BuildFriends(PlayerRecord player, PlayerManager players)
    {
        var state = players.GetFriendState(player);
        var friends = new Friends
        {
            Accepted = new FriendProfiles(),
            PendingSent = new FriendProfiles(),
            Receive = new FriendProfiles()
        };

        friends.Accepted.PlayerProfiles.Add(state.Accepted.Select(BuildSimpleProfile));
        friends.PendingSent.PlayerProfiles.Add(state.PendingSent.Select(BuildSimpleProfile));
        friends.Receive.PlayerProfiles.Add(state.Received.Select(BuildSimpleProfile));
        return friends;
    }

    private PlayerData BuildPlayerData(PlayerRecord player)
    {
        var initialData = master.GetInitialPlayerData(player.InitialDataGroup);
        var (decks, mainDeckId) = BuildDeckState(player, initialData);
        var mainDeck = decks.FirstOrDefault(deck => deck.Id == mainDeckId);
        var data = new PlayerData
        {
            MainDeck = mainDeckId,
            Gem = new Gem
            {
                Free = 10000,
                Paid = 0
            },
            LiveBoost = new LiveBoost
            {
                Amount = 10,
                PreviousRecoveryAt = UnixSeconds(player.CreatedAt),
                LastDailyResetAt = UnixSeconds(player.CreatedAt)
            },
            PlayerRankExp = InitialPlayerRankExp,
            LiveSkip = new LiveSkip(),
            PlayerMissionData = new PlayerMissionData(),
            LiveSetting = new LiveSetting
            {
                SettingAll = ByteString.CopyFrom(
                    player.LiveSettingAll is { Length: > 0 }
                        ? LiveSettingCodec.NormalizeLiveSettingAll(player.LiveSettingAll)
                        : LiveSettingCodec.CreateDefaultLiveSettingAll())
            },
            MyProfile = BuildSimpleProfile(player, initialData, mainDeck),
            LiveStampReward = new LiveStampReward(),
            Comeback = new Comeback()
        };
        ClientStateProtocol.InitializePlayer(data);

        if (player.CircleId != 0)
            data.CircleId = player.CircleId;

        for (var i = 0; i < initialData.MemberCards.Count; i++)
            data.MemberCards.Add(BuildMemberCard(i + 1, initialData.MemberCards[i], player.CreatedAt));

        for (var i = 0; i < initialData.SupportCards.Count; i++)
            data.SupportCards.Add(BuildSupportCard(i + 1, initialData.SupportCards[i], player.CreatedAt));

        foreach (var deck in decks)
            data.Decks.Add(deck.Clone());

        lock (player.LiveStateLock)
        {
            foreach (var score in master.LiveScoreSeeds)
            {
                data.LiveScore.Add(player.LiveScores.TryGetValue((score.MusicId, score.Difficulty), out var saved)
                    ? saved.Clone() : new LiveScore { MusicId = score.MusicId, Difficulty = score.Difficulty });
            }
        }

        foreach (var musicId in master.DefaultUnlockedLiveMusicIds)
        {
            data.LiveMusicReward.Add(new LiveMusicReward
            {
                MusicId = musicId
            });
        }

        foreach (var characterId in InitialCharacterIds)
        {
            data.CharacterRank.Add(new CharacterRank
            {
                CharacterId = characterId,
                Exp = 1
            });
        }

        foreach (var musicId in master.DefaultUnlockedLiveMusicIds)
        {
            foreach (var difficulty in master.LiveComboRewardDifficulties)
            {
                data.LiveMusicComboReward.Add(new LiveMusicComboReward
                {
                    MusicId = musicId,
                    Difficulty = difficulty
                });
            }
        }

        var (stamps, stampFavoriteNames) = BuildStampState(player);
        foreach (var stamp in stamps)
            data.Stamps.Add(stamp);
        foreach (var favoriteName in stampFavoriteNames)
            data.StampFavoriteNames.Add(favoriteName);

        StoryEpisode[] seenStoryEpisodes;
        StoryEpisode[] seenStoryFriendshipEpisodes;
        lock (player.StoryStateLock)
        {
            seenStoryEpisodes = player.SeenStoryEpisodes.Values
                .OrderBy(episode => episode.EpisodeId)
                .Select(episode => episode.Clone())
                .ToArray();

            seenStoryFriendshipEpisodes = player.SeenStoryFriendshipEpisodes.Values
                .OrderBy(episode => episode.EpisodeId)
                .Select(episode => episode.Clone())
                .ToArray();
        }

        data.SeenStoryEpisodes.Add(seenStoryEpisodes);
        data.SeenStoryFriendshipEpisodes.Add(seenStoryFriendshipEpisodes);

        foreach (var masterId in player.ShownCarouselHelpIds.Keys.Order())
            data.ShownCarouselHelps.Add(new CarouselHelp { MasterId = masterId });

        foreach (var masterId in player.ShownContentUnlockIds.Keys.Order())
            data.ShownContentUnlocks.Add(new ContentUnlock { MasterId = masterId });

        return data;
    }

    private static (IReadOnlyList<Stamp> Stamps, IReadOnlyList<StampFavoriteName> FavoriteNames) BuildStampState(
        PlayerRecord player)
    {
        long[] ownedStampIds;
        Dictionary<int, long[]> favoriteGroups;
        Dictionary<int, string> favoriteNames;
        lock (player.StampStateLock)
        {
            ownedStampIds = player.OwnedStampIds.Order().ToArray();
            favoriteGroups = player.StampFavoriteGroups.ToDictionary(
                pair => pair.Key,
                pair => (long[])pair.Value.Clone());
            favoriteNames = new Dictionary<int, string>(player.StampFavoriteNames);
        }

        var stamps = ownedStampIds.ToDictionary(id => id, id => new Stamp { Id = id });
        foreach (var (favoriteId, stampIds) in favoriteGroups.OrderBy(pair => pair.Key))
        {
            for (var index = 0; index < stampIds.Length; index++)
            {
                var stampId = stampIds[index];
                if (stampId <= 0 || !stamps.TryGetValue(stampId, out var stamp))
                    continue;

                stamp.Favorites.Add(new FavoriteIndex
                {
                    FavoriteId = favoriteId,
                    SlotIndex = index + 1
                });
            }
        }

        var names = favoriteNames
            .OrderBy(pair => pair.Key)
            .Select(pair => new StampFavoriteName
            {
                FavoriteId = pair.Key,
                Name = pair.Value
            })
            .ToArray();

        return (stamps.Values.OrderBy(stamp => stamp.Id).ToArray(), names);
    }

    private static PlayerSimpleProfile BuildSimpleProfile(
        PlayerRecord player,
        InitialPlayerData initialData,
        Deck? mainDeck)
    {
        var profile = new PlayerSimpleProfile
        {
            Id = player.PlayerId,
            Name = player.DisplayName,
            RankExp = InitialPlayerRankExp,
            LastUpdatedAt = player.ProfileUpdatedAtUnixSeconds,
            ProfileId = player.ProfileId
        };

        var requestedMemberCardId = player.FavoriteMemberCardId;
        var displayMemberCardId = requestedMemberCardId > 0
            ? requestedMemberCardId
            : mainDeck?.Cards.FirstOrDefault(card => card.SlotIndex == LeaderSlotIndex)?.MemberCardId ?? 0;
        if (displayMemberCardId < 1 || displayMemberCardId > initialData.MemberCards.Count)
            displayMemberCardId = initialData.MemberCards.Count > 0 ? 1 : 0;

        if (displayMemberCardId > 0)
        {
            var seed = initialData.MemberCards[(int)displayMemberCardId - 1];
            profile.FavoriteMemberCard = BuildDeckMemberCardDetail(seed);
            if (requestedMemberCardId > 0 && requestedMemberCardId <= initialData.MemberCards.Count)
                profile.FavoriteMemberCardMasterId = seed.MasterId;
        }

        return profile;
    }

    private static PlayerCredential BuildCredential(PlayerRecord player) => new()
    {
        Id = player.PlayerId,
        Credential = player.AuthorizationKey,
        DeviceId = player.DeviceId
    };

    private static MemberCard BuildMemberCard(int id, InitialMemberCardSeed seed, DateTimeOffset gainAt) => new()
    {
        Id = id,
        MasterId = seed.MasterId,
        Exp = seed.Exp,
        AwakeCount = seed.AwakeCount,
        CardRank = seed.CardRank,
        LeaderSkillLevel = seed.LeaderSkillLevel,
        LiveSkillLevel = seed.LiveSkillLevel,
        PerformanceSkillLevel = seed.PerformanceSkillLevel,
        GainAt = UnixSeconds(gainAt),
        LinkSkillLevel = seed.LinkSkillLevel,
        GekisouSkillLevel = seed.GekisouSkillLevel
    };

    private static DeckMemberCardDetail BuildDeckMemberCardDetail(InitialMemberCardSeed seed) => new()
    {
        CardId = seed.MasterId,
        Exp = seed.Exp,
        AwakeCount = seed.AwakeCount,
        CardRank = seed.CardRank,
        LiveSkillLevel = seed.LiveSkillLevel,
        PerformanceSkillLevel = seed.PerformanceSkillLevel
    };

    private static SupportCard BuildSupportCard(int id, InitialSupportCardSeed seed, DateTimeOffset gainAt) => new()
    {
        Id = id,
        MasterId = seed.MasterId,
        Exp = seed.Exp,
        CardRank = seed.CardRank,
        GainAt = UnixSeconds(gainAt),
        DuplicateCount = seed.DuplicateCount
    };

    private static Deck BuildDeck(InitialDeckSeed deck)
    {
        var result = new Deck
        {
            Id = deck.Id,
            Name = deck.Name
        };

        var cardCount = Math.Min(deck.MemberCardIds.Count, deck.SupportCardIds.Count);
        for (var i = 0; i < cardCount; i++)
        {
            result.Cards.Add(new DeckCard
            {
                SlotIndex = i,
                PerformanceOrderIndex = i,
                MemberCardId = deck.MemberCardIds[i],
                SupportCardId = deck.SupportCardIds[i]
            });
        }

        result.TotalPower = DeriveTotalPower(cardCount);

        return result;
    }

    private static (IReadOnlyList<Deck> Decks, int MainDeckId) BuildDeckState(
        PlayerRecord player,
        InitialPlayerData initialData)
    {
        Dictionary<int, Deck> overrides;
        int mainDeckOverride;

        lock (player.DeckStateLock)
        {
            overrides = player.DeckOverrides.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
            mainDeckOverride = player.MainDeckOverride;
        }

        var decks = initialData.Decks.Select(BuildDeck).ToList();
        var indices = new Dictionary<int, int>();
        for (var i = 0; i < decks.Count; i++)
            indices.TryAdd(decks[i].Id, i);

        foreach (var (id, state) in overrides.OrderBy(pair => pair.Key))
        {
            var projection = BuildSavedDeck(state);
            if (indices.TryGetValue(id, out var index))
            {
                decks[index] = projection;
            }
            else
            {
                indices[id] = decks.Count;
                decks.Add(projection);
            }
        }

        var initialMainDeckId = initialData.Decks.FirstOrDefault()?.Id ?? 1;
        return (decks, mainDeckOverride != 0 ? mainDeckOverride : initialMainDeckId);
    }

    private static Deck BuildSavedDeck(Deck state)
    {
        var result = state.Clone();
        result.TotalPower = DeriveTotalPower(result.Cards.Count);
        return result;
    }

    // The client omits totalPower when saving; keep it as a server-owned projection.
    private static int DeriveTotalPower(int cardCount) => cardCount > 0 ? ProjectedDeckTotalPower : 0;

    private static long UnixSeconds(DateTimeOffset value) => value.ToUnixTimeSeconds();
}
