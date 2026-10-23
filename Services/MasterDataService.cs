using System.Text.Json;
using MyNotes.Config;

namespace MyNotes.Services;

public sealed class MasterDataService
{
    private readonly ILogger<MasterDataService> _logger;
    private readonly Dictionary<string, InitialPlayerData> _initialDataByGroup;

    public string Version { get; }
    public string ResourceVersion { get; }
    public IReadOnlyList<long> DefaultUnlockedLiveMusicIds { get; }
    public IReadOnlyList<LiveScoreSeed> LiveScoreSeeds { get; }
    public IReadOnlyList<int> LiveComboRewardDifficulties { get; }

    public MasterDataService(IHostEnvironment environment, ILogger<MasterDataService> logger, ResourceReleaseService release)
    {
        _logger = logger;
        Version = release.MasterVersion;
        ResourceVersion = release.ResourceVersion;

        var masterDirectory = release.MasterJsonDirectory ?? ResolveMasterDirectory(environment.ContentRootPath);
        if (masterDirectory == null)
        {
            _logger.LogWarning("Master data directory was not found; using baked fallback ids.");
            _initialDataByGroup = BuildFallbackInitialData();
            DefaultUnlockedLiveMusicIds = FallbackLiveMusicIds;
            LiveScoreSeeds = FallbackLiveScoreSeeds;
            LiveComboRewardDifficulties = FallbackLiveDifficulties;
            return;
        }

        _initialDataByGroup = LoadInitialPlayerData(masterDirectory);
        DefaultUnlockedLiveMusicIds = LoadLiveMusicIds(masterDirectory);
        LiveScoreSeeds = LoadLiveScoreSeeds(masterDirectory);
        LiveComboRewardDifficulties = LoadComboRewardDifficulties(masterDirectory);

        _logger.LogInformation(
            "Loaded master data from {Path}: groups={GroupCount}, liveMusic={LiveMusicCount}, liveScore={LiveScoreCount}, comboDifficulties={ComboDifficultyCount}",
            masterDirectory,
            _initialDataByGroup.Count,
            DefaultUnlockedLiveMusicIds.Count,
            LiveScoreSeeds.Count,
            LiveComboRewardDifficulties.Count);
    }

    public InitialPlayerData GetInitialPlayerData(string? initialDataGroup)
    {
        var group = ResolveInitialGroup(initialDataGroup);
        if (_initialDataByGroup.TryGetValue(group, out var data))
            return data;

        if (_initialDataByGroup.TryGetValue("Default", out data))
            return data;

        return _initialDataByGroup.Values.First();
    }

    private static string? ResolveMasterDirectory(string contentRootPath)
    {
        var candidates = new[]
        {
            Path.Combine(contentRootPath, ServerConfig.DataDirectory, "master")
        };

        return candidates.FirstOrDefault(path =>
            File.Exists(Path.Combine(path, "MasterPlayerData.json")) &&
            File.Exists(Path.Combine(path, "MasterLiveMusic.json")) &&
            File.Exists(Path.Combine(path, "MasterLiveMusicScore.json")));
    }

    private static string ResolveInitialGroup(string? initialDataGroup)
    {
        if (initialDataGroup?.Contains("cbt", StringComparison.OrdinalIgnoreCase) == true)
            return "CBT";

        return "Default";
    }

    private static Dictionary<string, InitialPlayerData> LoadInitialPlayerData(string masterDirectory)
    {
        var rows = ReadRows(Path.Combine(masterDirectory, "MasterPlayerData.json"));
        var memberMasterIds = ReadIdSet(masterDirectory, "MasterMemberCard.json");
        var supportMasterIds = ReadIdSet(masterDirectory, "MasterSupportCard.json");
        var result = new Dictionary<string, InitialPlayerData>(StringComparer.Ordinal);

        foreach (var groupRows in rows.GroupBy(row => GetString(row, "_group")))
        {
            var members = new List<InitialMemberCardSeed>();
            var supports = new List<InitialSupportCardSeed>();
            var decks = new List<InitialDeckSeed>();

            foreach (var row in groupRows.OrderBy(row => GetLong(row, "_id")))
            {
                switch (GetInt(row, "_playerDataType"))
                {
                    case 1:
                    {
                        var masterId = GetParameterLong(row, 1);
                        if (!memberMasterIds.Contains(masterId))
                            continue;

                        members.Add(new InitialMemberCardSeed(
                            masterId,
                            Exp: GetParameterInt(row, 2),
                            AwakeCount: GetParameterInt(row, 3, 1),
                            CardRank: GetParameterInt(row, 4, 1),
                            LeaderSkillLevel: GetParameterInt(row, 5, 1),
                            LiveSkillLevel: GetParameterInt(row, 6, 1),
                            PerformanceSkillLevel: GetParameterInt(row, 7, 1),
                            LinkSkillLevel: GetParameterInt(row, 8, 1),
                            GekisouSkillLevel: 1));
                        break;
                    }
                    case 2:
                    {
                        var masterId = GetParameterLong(row, 1);
                        if (!supportMasterIds.Contains(masterId))
                            continue;

                        supports.Add(new InitialSupportCardSeed(
                            masterId,
                            Exp: GetParameterInt(row, 2),
                            CardRank: GetParameterInt(row, 3, 1),
                            DuplicateCount: GetParameterInt(row, 4)));
                        break;
                    }
                    case 5:
                    {
                        var deckId = GetParameterInt(row, 0);
                        if (deckId <= 0)
                            continue;

                        decks.Add(new InitialDeckSeed(
                            deckId,
                            GetParameterString(row, 1, $"Deck {deckId}"),
                            ParsePositiveIds(GetParameterString(row, 2)),
                            ParsePositiveIds(GetParameterString(row, 3))));
                        break;
                    }
                }
            }

            result[groupRows.Key] = new InitialPlayerData(members, supports, decks);
        }

        if (result.Count == 0)
            result["Default"] = new InitialPlayerData([], [], []);
        return result;
    }

    private static IReadOnlyList<long> LoadLiveMusicIds(string masterDirectory)
    {
        var ids = ReadRows(Path.Combine(masterDirectory, "MasterLiveMusic.json"))
            .Where(row => GetBool(row, "_defaultUnlock", true))
            .Select(row => GetLong(row, "_id"))
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToArray();

        return ids;
    }

    private static IReadOnlyList<LiveScoreSeed> LoadLiveScoreSeeds(string masterDirectory)
    {
        var liveMusicIds = LoadLiveMusicIds(masterDirectory).ToHashSet();
        var scores = ReadRows(Path.Combine(masterDirectory, "MasterLiveMusicScore.json"))
            .Select(row => GetLong(row, "_id")).Where(id => id > 0).ToHashSet();
        var seeds = new List<LiveScoreSeed>();
        var fields = new[] { "_easyID", "_normalID", "_hardID", "_expertID" };
        foreach (var music in ReadRows(Path.Combine(masterDirectory, "MasterLiveMusic.json")))
        {
            var id = GetLong(music, "_id");
            if (!liveMusicIds.Contains(id))
                continue;
            for (var index = 0; index < fields.Length; index++)
            {
                var scoreId = GetLong(music, fields[index]);
                if (scoreId > 0 && !scores.Contains(scoreId))
                    throw new InvalidDataException("song references a missing score");
                if (scoreId > 0)
                    seeds.Add(new LiveScoreSeed(id, index + 1));
            }
        }
        return seeds.Distinct().OrderBy(seed => seed.MusicId).ThenBy(seed => seed.Difficulty).ToArray();
    }

    private static IReadOnlyList<int> LoadComboRewardDifficulties(string masterDirectory)
    {
        var difficulties = ReadRows(Path.Combine(masterDirectory, "MasterLiveMusicComboReward.json"))
            .Select(row => GetInt(row, "_difficulty"))
            .Select(ToServerDifficulty)
            .Where(difficulty => difficulty > 0)
            .Distinct()
            .Order()
            .ToArray();

        return difficulties;
    }

    private static HashSet<long> ReadIdSet(string masterDirectory, string fileName) =>
        ReadRows(Path.Combine(masterDirectory, fileName))
            .Select(row => GetLong(row, "_id"))
            .Where(id => id > 0)
            .ToHashSet();

    private static JsonElement[] ReadRows(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("_allData").EnumerateArray().Select(row => row.Clone()).ToArray();
    }

    private static long GetLong(JsonElement row, string propertyName)
    {
        if (!row.TryGetProperty(propertyName, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static int GetInt(JsonElement row, string propertyName, int defaultValue = 0)
    {
        var value = GetLong(row, propertyName);
        return value == 0 ? defaultValue : (int)value;
    }

    private static bool GetBool(JsonElement row, string propertyName, bool defaultValue = false)
    {
        if (!row.TryGetProperty(propertyName, out var value))
            return defaultValue;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static string GetString(JsonElement row, string propertyName, string defaultValue = "")
    {
        if (!row.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return defaultValue;

        return value.GetString() ?? defaultValue;
    }

    private static string GetParameterString(JsonElement row, int index, string defaultValue = "") =>
        GetString(row, $"_parameter{index}", defaultValue);

    private static long GetParameterLong(JsonElement row, int index, long defaultValue = 0)
    {
        var value = GetParameterString(row, index);
        return long.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static int GetParameterInt(JsonElement row, int index, int defaultValue = 0)
    {
        var value = GetParameterLong(row, index, defaultValue);
        return value == 0 ? defaultValue : (int)value;
    }

    private static long[] ParsePositiveIds(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => long.TryParse(part, out var parsed) ? parsed : -1)
            .Where(id => id > 0)
            .ToArray();

    private static int ToServerDifficulty(int masterDifficulty) =>
        masterDifficulty is >= 0 and <= 3 ? masterDifficulty + 1 : 0;

    private static Dictionary<string, InitialPlayerData> BuildFallbackInitialData()
    {
        var memberIds = new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 51, 60 };
        var supportIds = new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 51, 53 };
        var data = new InitialPlayerData(
            memberIds.Select(id => new InitialMemberCardSeed(id, 0, 1, 1, 1, 1, 1, 1, 1)).ToArray(),
            supportIds.Select(id => new InitialSupportCardSeed(id, 0, 1, 0)).ToArray(),
            [new InitialDeckSeed(1, "Main", [2, 4, 1, 5, 3], [2, 4, 1, 5, 3])]);

        return new Dictionary<string, InitialPlayerData>(StringComparer.Ordinal)
        {
            ["Default"] = data,
            ["CBT"] = data
        };
    }

    private static readonly long[] FallbackLiveMusicIds =
    [
        100001, 100002, 100003, 100004, 100007, 100008, 100009, 100011, 100012, 100013, 100014,
        100016, 100017, 100018, 100026, 100027, 100028, 100029, 100030, 100031, 100032, 100038,
        100039, 100040, 100042, 100050, 100051, 100052, 100053, 100054, 100055, 100069, 100080
    ];

    private static readonly int[] FallbackLiveDifficulties = [1, 2, 3, 4];

    private static readonly LiveScoreSeed[] FallbackLiveScoreSeeds =
        FallbackLiveMusicIds.SelectMany(id => FallbackLiveDifficulties.Select(difficulty => new LiveScoreSeed(id, difficulty))).ToArray();
}

public sealed record InitialPlayerData(
    IReadOnlyList<InitialMemberCardSeed> MemberCards,
    IReadOnlyList<InitialSupportCardSeed> SupportCards,
    IReadOnlyList<InitialDeckSeed> Decks);

public sealed record InitialMemberCardSeed(
    long MasterId,
    int Exp,
    int AwakeCount,
    int CardRank,
    int LeaderSkillLevel,
    int LiveSkillLevel,
    int PerformanceSkillLevel,
    int LinkSkillLevel,
    int GekisouSkillLevel);

public sealed record InitialSupportCardSeed(long MasterId, int Exp, int CardRank, int DuplicateCount);

public sealed record InitialDeckSeed(int Id, string Name, IReadOnlyList<long> MemberCardIds, IReadOnlyList<long> SupportCardIds);

public sealed record LiveScoreSeed(long MusicId, int Difficulty);
