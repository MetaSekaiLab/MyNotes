using System.Collections.Concurrent;
using System.Security.Cryptography;
using App.Protobuf.Entity;
using MyNotes.Config;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class PlayerManager(ILogger<PlayerManager> logger)
{
    public sealed record CircleSnapshot(
        Circle Circle,
        PlayerRecord Master,
        PlayerRecord[] Members,
        string[] SubmasterPlayerIds)
    {
        public CircleAuth GetAuth(PlayerRecord player) => ReferenceEquals(player, Master)
            ? CircleAuth.Master
            : SubmasterPlayerIds.Contains(player.PlayerId)
                ? CircleAuth.Submaster
                : CircleAuth.Normal;
    }

    public enum CircleJoinOutcome
    {
        MissingCircle,
        Joined,
        Pending,
        Rejected
    }

    private const int FavoriteStampGroupCount = 3;
    private const int FavoriteStampNameMaxLength = 6;
    private const int FavoriteStampSlotCount = 20;
    private const int CircleSubmasterMaxCount = 2;
    private readonly ConcurrentDictionary<string, PlayerRecord> _playersById = new();
    private readonly ConcurrentDictionary<long, PlayerRecord> _playersByProfileId = new();
    private readonly ConcurrentDictionary<string, PlayerRecord> _playersByAuthorization = new(StringComparer.Ordinal);
    private readonly object _invitationStateLock = new();
    private readonly object _friendStateLock = new();
    private readonly object _circleStateLock = new();
    private ulong _nextCircleId = 1;
    private long _nextProfileId = 100000000;

    public PlayerRecord Register(string initialDataGroup)
    {
        var profileId = Interlocked.Increment(ref _nextProfileId);
        var now = DateTimeOffset.UtcNow;
        var player = new PlayerRecord
        {
            ProfileId = profileId,
            PlayerId = profileId.ToString(),
            DisplayName = $"Player{profileId % 10000:D4}",
            AuthorizationKey = NewToken(32),
            DeviceId = NewToken(16),
            InitialDataGroup = string.IsNullOrWhiteSpace(initialDataGroup) ? ServerConfig.InitialDataGroup : initialDataGroup,
            LiveSettingAll = LiveSettingCodec.CreateDefaultLiveSettingAll(),
            CreatedAt = now,
            ProfileUpdatedAtUnixSeconds = now.ToUnixTimeSeconds()
        };

        foreach (var stampId in ServerConfig.InitialStampIds)
            player.OwnedStampIds.Add(stampId);

        Add(player);
        logger.LogInformation("Registered player {PlayerId} profile {ProfileId}", player.PlayerId, player.ProfileId);
        return player;
    }

    public PlayerRecord GetFromRequest(HttpRequest request)
    {
        if (TryGetFromRequest(request, out var player))
            return player;

        throw new UnauthorizedAccessException("Valid player credentials are required.");
    }

    public bool TryGetByProfileId(long profileId, out PlayerRecord player) =>
        _playersByProfileId.TryGetValue(profileId, out player!);

    public static string GetInvitationCode(PlayerRecord player) => player.ProfileId.ToString("D10");

    public bool InputInvitationCode(PlayerRecord invitee, string invitationCode)
    {
        if (invitationCode.Length != 10 ||
            invitationCode.Any(character => character is < '0' or > '9') ||
            !long.TryParse(invitationCode, out var inviterProfileId))
            return false;

        lock (_invitationStateLock)
        {
            if (invitee.IsInvitationCodeInputAlready ||
                !_playersByProfileId.TryGetValue(inviterProfileId, out var inviter) ||
                ReferenceEquals(inviter, invitee))
                return false;

            invitee.IsInvitationCodeInputAlready = true;
            var establishmentAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            inviter.InvitationEstablishments[invitee.PlayerId] = establishmentAt;
            inviter.NewInvitationPlayerIds.Add(invitee.PlayerId);
            return true;
        }
    }

    public void MarkInvitationsViewed(PlayerRecord player, IEnumerable<string> playerIds)
    {
        lock (_invitationStateLock)
        {
            foreach (var playerId in playerIds)
                player.NewInvitationPlayerIds.Remove(playerId);
        }
    }

    public (bool InputAlready, string[] NewPlayerIds) GetInvitationState(PlayerRecord player)
    {
        lock (_invitationStateLock)
            return (player.IsInvitationCodeInputAlready, player.NewInvitationPlayerIds.ToArray());
    }

    public (PlayerRecord Player, long EstablishmentAt)[] GetInvitationProfiles(PlayerRecord player)
    {
        lock (_invitationStateLock)
        {
            return player.InvitationEstablishments
                .Select(pair => (_playersById[pair.Key], pair.Value))
                .ToArray();
        }
    }

    public void LogPlayerReport(
        PlayerRecord reporter,
        string targetPlayerId,
        int reportType,
        IEnumerable<long> reportIds)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var reportIdSnapshot = reportIds.ToArray();
        logger.LogInformation(
            "Player report received at {ReceivedAt} from {ReporterPlayerId} for {TargetPlayerId} " +
            "(type {ReportType}, report ids [{ReportIds}])",
            receivedAt,
            reporter.PlayerId,
            targetPlayerId,
            reportType,
            string.Join(',', reportIdSnapshot));
    }

    public (PlayerRecord? Target, bool IsAccepted) RequestFriend(PlayerRecord requester, string targetPlayerId)
    {
        lock (_friendStateLock)
        {
            if (!_playersById.TryGetValue(targetPlayerId, out var target) || ReferenceEquals(requester, target))
                return (null, false);

            if (requester.AcceptedFriendPlayerIds.Contains(target.PlayerId))
                return (target, true);

            var isAccepted = target.PendingSentFriendPlayerIds.Remove(requester.PlayerId);
            if (isAccepted)
            {
                requester.ReceivedFriendPlayerIds.Remove(target.PlayerId);
                requester.AcceptedFriendPlayerIds.Add(target.PlayerId);
                target.AcceptedFriendPlayerIds.Add(requester.PlayerId);
            }
            else
            {
                requester.PendingSentFriendPlayerIds.Add(target.PlayerId);
                target.ReceivedFriendPlayerIds.Add(requester.PlayerId);
            }

            return (target, isAccepted);
        }
    }

    public (PlayerRecord[] Accepted, PlayerRecord[] PendingSent, PlayerRecord[] Received) GetFriendState(PlayerRecord player)
    {
        lock (_friendStateLock)
        {
            return (
                ResolvePlayers(player.AcceptedFriendPlayerIds),
                ResolvePlayers(player.PendingSentFriendPlayerIds),
                ResolvePlayers(player.ReceivedFriendPlayerIds));
        }
    }

    public void WithdrawFriendRequest(PlayerRecord requester, string targetPlayerId)
    {
        lock (_friendStateLock)
        {
            requester.PendingSentFriendPlayerIds.Remove(targetPlayerId);
            if (_playersById.TryGetValue(targetPlayerId, out var target))
                target.ReceivedFriendPlayerIds.Remove(requester.PlayerId);
        }
    }

    public PlayerRecord[] AnswerFriendRequests(PlayerRecord responder, IEnumerable<string> targetPlayerIds, bool answer)
    {
        lock (_friendStateLock)
        {
            var accepted = new List<PlayerRecord>();
            foreach (var targetPlayerId in targetPlayerIds.Distinct(StringComparer.Ordinal))
            {
                if (!_playersById.TryGetValue(targetPlayerId, out var requester) ||
                    !responder.ReceivedFriendPlayerIds.Remove(requester.PlayerId))
                    continue;

                requester.PendingSentFriendPlayerIds.Remove(responder.PlayerId);
                if (answer)
                {
                    responder.AcceptedFriendPlayerIds.Add(requester.PlayerId);
                    requester.AcceptedFriendPlayerIds.Add(responder.PlayerId);
                    accepted.Add(requester);
                }
            }

            return accepted.OrderBy(player => player.ProfileId).ToArray();
        }
    }

    public void UnlinkFriend(PlayerRecord player, string friendPlayerId)
    {
        lock (_friendStateLock)
        {
            player.AcceptedFriendPlayerIds.Remove(friendPlayerId);
            if (_playersById.TryGetValue(friendPlayerId, out var friend))
                friend.AcceptedFriendPlayerIds.Remove(player.PlayerId);
        }
    }

    public ulong CreateCircle(PlayerRecord player, SaveCircleParams parameters)
    {
        lock (_circleStateLock)
        {
            if (player.CircleId != 0)
                return player.CircleId;

            var circleId = _nextCircleId++;
            ClearPendingApplicantUnsafe(player.PlayerId);
            RemoveCircleInvitationEdgesUnsafe(player);
            ClearCircleSubmasterUnsafe(player.PlayerId);
            player.CircleSubmasterPlayerIds.Clear();
            player.CircleId = circleId;
            player.OwnedCircle = new Circle
            {
                Id = circleId,
                Name = parameters.Name,
                Description = parameters.Description,
                JoinRule = (uint)parameters.JoinRule,
                PlayStyle = (uint)parameters.PlayStyle,
                MemberCount = 1
            };
            return circleId;
        }
    }

    public void EditCircle(PlayerRecord player, SaveCircleParams parameters)
    {
        lock (_circleStateLock)
        {
            if (player.OwnedCircle == null)
                return;

            player.OwnedCircle.Name = parameters.Name;
            player.OwnedCircle.Description = parameters.Description;
            player.OwnedCircle.JoinRule = (uint)parameters.JoinRule;
            player.OwnedCircle.PlayStyle = (uint)parameters.PlayStyle;
        }
    }

    public void DeleteCircle(PlayerRecord player)
    {
        lock (_circleStateLock)
        {
            var circleId = player.OwnedCircle?.Id ?? 0;
            if (circleId == 0)
                return;

            foreach (var other in _playersById.Values)
            {
                if (other.CircleId == circleId)
                    other.CircleId = 0;

                other.OutgoingCircleInvitationPlayerIds.Remove(player.PlayerId);
                other.IncomingCircleInviterPlayerIds.Remove(player.PlayerId);
            }

            player.PendingCircleApplicantIds.Clear();
            player.OutgoingCircleInvitationPlayerIds.Clear();
            player.IncomingCircleInviterPlayerIds.Clear();
            player.CircleSubmasterPlayerIds.Clear();
            player.CircleId = 0;
            player.OwnedCircle = null;
        }
    }

    public void ExitCircle(PlayerRecord player)
    {
        lock (_circleStateLock)
        {
            var circle = BuildCircleSnapshotUnsafe(player.CircleId);
            if (player.OwnedCircle != null ||
                circle == null ||
                !circle.Members.Contains(player))
                return;

            circle.Master.CircleSubmasterPlayerIds.Remove(player.PlayerId);
            player.CircleId = 0;
            UpdateCircleMemberCountUnsafe(circle.Master);
        }
    }

    public void RemoveCirclePlayer(PlayerRecord requester, string playerId)
    {
        lock (_circleStateLock)
        {
            if (requester.OwnedCircle == null ||
                !_playersById.TryGetValue(playerId, out var target) ||
                ReferenceEquals(requester, target) ||
                target.OwnedCircle != null ||
                target.CircleId != requester.OwnedCircle.Id)
                return;

            requester.CircleSubmasterPlayerIds.Remove(target.PlayerId);
            target.CircleId = 0;
            UpdateCircleMemberCountUnsafe(requester);
        }
    }

    public void SetCircleSubmaster(PlayerRecord requester, string playerId)
    {
        lock (_circleStateLock)
        {
            if (requester.OwnedCircle == null ||
                !_playersById.TryGetValue(playerId, out var target) ||
                ReferenceEquals(requester, target) ||
                target.OwnedCircle != null ||
                target.CircleId != requester.OwnedCircle.Id ||
                requester.CircleSubmasterPlayerIds.Contains(target.PlayerId) ||
                requester.CircleSubmasterPlayerIds.Count >= CircleSubmasterMaxCount)
                return;

            requester.CircleSubmasterPlayerIds.Add(target.PlayerId);
        }
    }

    public void TransferCircleMaster(PlayerRecord requester, string playerId)
    {
        lock (_circleStateLock)
        {
            var circle = requester.OwnedCircle;
            if (circle == null ||
                requester.CircleId != circle.Id ||
                !_playersById.TryGetValue(playerId, out var target) ||
                ReferenceEquals(requester, target) ||
                target.OwnedCircle != null ||
                target.CircleId != circle.Id)
                return;

            var targetWasSubmaster = requester.CircleSubmasterPlayerIds.Remove(target.PlayerId);
            target.CircleSubmasterPlayerIds.Clear();
            target.CircleSubmasterPlayerIds.UnionWith(requester.CircleSubmasterPlayerIds);
            target.CircleSubmasterPlayerIds.Remove(requester.PlayerId);
            target.CircleSubmasterPlayerIds.Remove(target.PlayerId);
            if (targetWasSubmaster)
                target.CircleSubmasterPlayerIds.Add(requester.PlayerId);
            requester.CircleSubmasterPlayerIds.Clear();

            target.PendingCircleApplicantIds.Clear();
            target.PendingCircleApplicantIds.UnionWith(requester.PendingCircleApplicantIds);
            requester.PendingCircleApplicantIds.Clear();

            foreach (var invitedPlayerId in target.OutgoingCircleInvitationPlayerIds)
            {
                if (_playersById.TryGetValue(invitedPlayerId, out var invitedPlayer))
                    invitedPlayer.IncomingCircleInviterPlayerIds.Remove(target.PlayerId);
            }
            target.OutgoingCircleInvitationPlayerIds.Clear();
            target.OutgoingCircleInvitationPlayerIds.UnionWith(requester.OutgoingCircleInvitationPlayerIds);
            foreach (var invitedPlayerId in target.OutgoingCircleInvitationPlayerIds)
            {
                if (!_playersById.TryGetValue(invitedPlayerId, out var invitedPlayer))
                    continue;

                invitedPlayer.IncomingCircleInviterPlayerIds.Remove(requester.PlayerId);
                invitedPlayer.IncomingCircleInviterPlayerIds.Add(target.PlayerId);
            }
            requester.OutgoingCircleInvitationPlayerIds.Clear();

            requester.OwnedCircle = null;
            target.OwnedCircle = circle;
        }
    }

    public void TransferCircleSubmaster(PlayerRecord requester, string playerId)
    {
        lock (_circleStateLock)
        {
            var circle = BuildCircleSnapshotUnsafe(requester.CircleId);
            if (requester.OwnedCircle != null ||
                circle == null ||
                !circle.Members.Contains(requester) ||
                !circle.SubmasterPlayerIds.Contains(requester.PlayerId) ||
                !_playersById.TryGetValue(playerId, out var target) ||
                ReferenceEquals(requester, target) ||
                target.OwnedCircle != null ||
                target.CircleId != circle.Circle.Id ||
                circle.SubmasterPlayerIds.Contains(target.PlayerId))
                return;

            circle.Master.CircleSubmasterPlayerIds.Remove(requester.PlayerId);
            circle.Master.CircleSubmasterPlayerIds.Add(target.PlayerId);
        }
    }

    public void UnsetCircleSubmaster(PlayerRecord requester, string playerId)
    {
        lock (_circleStateLock)
        {
            if (requester.OwnedCircle == null ||
                !_playersById.TryGetValue(playerId, out var target) ||
                ReferenceEquals(requester, target) ||
                target.OwnedCircle != null ||
                target.CircleId != requester.OwnedCircle.Id)
                return;

            requester.CircleSubmasterPlayerIds.Remove(target.PlayerId);
        }
    }

    public (Circle Circle, PlayerRecord Master)? GetCircleDetail(ulong circleId)
    {
        var snapshot = GetCircleSnapshot(circleId);
        return snapshot == null ? null : (snapshot.Circle, snapshot.Master);
    }

    public CircleSnapshot? GetCircleSnapshot(ulong circleId)
    {
        lock (_circleStateLock)
            return BuildCircleSnapshotUnsafe(circleId);
    }

    public App.Protobuf.Entity.CircleAuth GetCircleAuth(PlayerRecord player)
    {
        lock (_circleStateLock)
        {
            var snapshot = BuildCircleSnapshotUnsafe(player.CircleId);
            if (snapshot == null || !snapshot.Members.Contains(player))
                return App.Protobuf.Entity.CircleAuth.Normal;

            return snapshot.GetAuth(player);
        }
    }

    public CircleJoinOutcome JoinCircle(PlayerRecord player, ulong circleId)
    {
        lock (_circleStateLock)
        {
            var master = _playersById.Values.FirstOrDefault(candidate => candidate.OwnedCircle?.Id == circleId);
            if (circleId == 0 || master?.OwnedCircle == null)
                return CircleJoinOutcome.MissingCircle;

            if (player.CircleId == circleId)
            {
                ClearPendingApplicantUnsafe(player.PlayerId);
                RemoveCircleInvitationEdgesUnsafe(player);
                UpdateCircleMemberCountUnsafe(master);
                return CircleJoinOutcome.Joined;
            }

            if (player.CircleId != 0)
                return CircleJoinOutcome.Rejected;

            var hasInvitation = master.OutgoingCircleInvitationPlayerIds.Contains(player.PlayerId) &&
                player.IncomingCircleInviterPlayerIds.Contains(master.PlayerId);
            var joinRule = (JoinRule)master.OwnedCircle.JoinRule;
            switch (joinRule)
            {
                case JoinRule.Request when !hasInvitation:
                    master.PendingCircleApplicantIds.Add(player.PlayerId);
                    return CircleJoinOutcome.Pending;
                case JoinRule.Invite when !hasInvitation:
                    return CircleJoinOutcome.Rejected;
                case JoinRule.Auto:
                case JoinRule.Request:
                case JoinRule.Invite:
                case JoinRule.Unspecified:
                    break;
                default:
                    return CircleJoinOutcome.Rejected;
            }

            EstablishCircleMembershipUnsafe(player, master);
            return CircleJoinOutcome.Joined;
        }
    }

    public void ApproveCircleJoinRequest(PlayerRecord requester, string playerId)
    {
        lock (_circleStateLock)
        {
            if (requester.OwnedCircle == null ||
                !requester.PendingCircleApplicantIds.Contains(playerId) ||
                !_playersById.TryGetValue(playerId, out var applicant) ||
                applicant.CircleId != 0)
                return;

            EstablishCircleMembershipUnsafe(applicant, requester);
        }
    }

    public void RevokeCircleJoinRequest(PlayerRecord requester, string playerId)
    {
        lock (_circleStateLock)
        {
            if (requester.OwnedCircle == null)
                return;

            requester.PendingCircleApplicantIds.Remove(playerId);
        }
    }

    public PlayerRecord[] GetCircleJoinRequests(PlayerRecord player)
    {
        lock (_circleStateLock)
        {
            if (player.OwnedCircle == null)
                return [];

            var applicants = new List<PlayerRecord>();
            foreach (var applicantId in player.PendingCircleApplicantIds.ToArray())
            {
                if (!_playersById.TryGetValue(applicantId, out var applicant) || applicant.CircleId != 0)
                {
                    player.PendingCircleApplicantIds.Remove(applicantId);
                    continue;
                }

                applicants.Add(applicant);
            }

            return applicants.OrderBy(applicant => applicant.ProfileId).ToArray();
        }
    }

    public (Circle Circle, PlayerRecord Master)[] GetRecommendedCircles(PlayerRecord player)
    {
        lock (_circleStateLock)
        {
            return _playersById.Values
                .Where(master => !ReferenceEquals(master, player) && master.OwnedCircle != null)
                .Select(master => BuildCircleSnapshotUnsafe(master.OwnedCircle!.Id))
                .Where(snapshot => snapshot != null)
                .Select(snapshot => (Circle: snapshot!.Circle, Master: snapshot.Master))
                .OrderBy(item => item.Circle.Id)
                .ToArray();
        }
    }

    public (Circle Circle, PlayerRecord Master)[] SearchCircles(PlayerRecord player, SearchOptions? options)
    {
        var circles = GetRecommendedCircles(player);
        return options == null
            ? circles
            : circles.Where(item => MatchesCircleSearch(item.Circle, options)).ToArray();
    }

    private static bool MatchesCircleSearch(Circle circle, SearchOptions options)
    {
        if (!string.IsNullOrEmpty(options.Name) &&
            !circle.Name.Contains(options.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        if (options.JoinRule != JoinRule.Unspecified && circle.JoinRule != (uint)options.JoinRule)
            return false;

        if (options.PlayStyle != PlayStyle.Unspecified && circle.PlayStyle != (uint)options.PlayStyle)
            return false;

        return options.MemberRange switch
        {
            MemberRange.Unspecified => true,
            MemberRange._1 => circle.MemberCount is >= 1 and <= 10,
            MemberRange._2 => circle.MemberCount is >= 11 and <= 20,
            MemberRange._3 => circle.MemberCount is >= 21 and <= 30,
            _ => false
        };
    }

    public PlayerRecord? GetInvitableCirclePlayer(PlayerRecord requester, string playerId)
    {
        lock (_circleStateLock)
        {
            if (requester.OwnedCircle == null ||
                !_playersById.TryGetValue(playerId, out var target) ||
                ReferenceEquals(requester, target) ||
                target.CircleId != 0)
                return null;

            return target;
        }
    }

    public PlayerRecord[] GetCircleInvitationCandidates(PlayerRecord requester)
    {
        lock (_circleStateLock)
        {
            if (requester.OwnedCircle == null)
                return [];

            return _playersById.Values
                .Where(player => !ReferenceEquals(player, requester) &&
                    player.CircleId == 0 &&
                    !requester.OutgoingCircleInvitationPlayerIds.Contains(player.PlayerId))
                .OrderBy(player => player.ProfileId)
                .ToArray();
        }
    }

    public void InviteCirclePlayer(PlayerRecord requester, string playerId)
    {
        lock (_circleStateLock)
        {
            if (requester.OwnedCircle == null ||
                !_playersById.TryGetValue(playerId, out var target) ||
                ReferenceEquals(requester, target) ||
                target.CircleId != 0)
                return;

            requester.OutgoingCircleInvitationPlayerIds.Add(target.PlayerId);
            target.IncomingCircleInviterPlayerIds.Add(requester.PlayerId);
        }
    }

    public void RevokeCircleInvitation(PlayerRecord requester, string playerId)
    {
        lock (_circleStateLock)
        {
            if (requester.OwnedCircle == null)
                return;

            requester.OutgoingCircleInvitationPlayerIds.Remove(playerId);
            if (_playersById.TryGetValue(playerId, out var target))
                target.IncomingCircleInviterPlayerIds.Remove(requester.PlayerId);
        }
    }

    public PlayerRecord[] GetInvitedCirclePlayers(PlayerRecord requester)
    {
        lock (_circleStateLock)
            return requester.OwnedCircle == null
                ? []
                : ResolvePlayers(requester.OutgoingCircleInvitationPlayerIds);
    }

    public (Circle Circle, PlayerRecord Inviter)[] GetIncomingCircleInvitations(PlayerRecord player)
    {
        lock (_circleStateLock)
        {
            if (player.CircleId != 0)
                return [];

            return ResolvePlayers(player.IncomingCircleInviterPlayerIds)
                .Where(inviter => inviter.OwnedCircle != null)
                .Select(inviter => BuildCircleSnapshotUnsafe(inviter.OwnedCircle!.Id))
                .Where(snapshot => snapshot != null)
                .Select(snapshot => (Circle: snapshot!.Circle, Inviter: snapshot.Master))
                .OrderBy(item => item.Circle.Id)
                .ToArray();
        }
    }

    private CircleSnapshot? BuildCircleSnapshotUnsafe(ulong circleId)
    {
        if (circleId == 0)
            return null;

        var master = _playersById.Values.FirstOrDefault(player => player.OwnedCircle?.Id == circleId);
        if (master?.OwnedCircle == null)
            return null;

        var members = _playersById.Values
            .Where(player => player.CircleId == circleId)
            .OrderBy(player => ReferenceEquals(player, master) ? 0 : 1)
            .ThenBy(player => player.ProfileId)
            .ToArray();
        var circle = master.OwnedCircle.Clone();
        circle.MemberCount = (uint)members.Length;
        var submasterPlayerIds = members
            .Where(member => master.CircleSubmasterPlayerIds.Contains(member.PlayerId))
            .Select(member => member.PlayerId)
            .ToArray();
        return new CircleSnapshot(circle, master, members, submasterPlayerIds);
    }

    private void UpdateCircleMemberCountUnsafe(PlayerRecord master)
    {
        if (master.OwnedCircle == null)
            return;

        master.OwnedCircle.MemberCount = (uint)_playersById.Values.Count(player => player.CircleId == master.OwnedCircle.Id);
    }

    private void EstablishCircleMembershipUnsafe(PlayerRecord player, PlayerRecord master)
    {
        ClearCircleSubmasterUnsafe(player.PlayerId);
        player.CircleId = master.OwnedCircle!.Id;
        ClearPendingApplicantUnsafe(player.PlayerId);
        RemoveCircleInvitationEdgesUnsafe(player);
        UpdateCircleMemberCountUnsafe(master);
    }

    private void ClearPendingApplicantUnsafe(string playerId)
    {
        foreach (var owner in _playersById.Values)
            owner.PendingCircleApplicantIds.Remove(playerId);
    }

    private void ClearCircleSubmasterUnsafe(string playerId)
    {
        foreach (var owner in _playersById.Values)
            owner.CircleSubmasterPlayerIds.Remove(playerId);
    }

    private void RemoveCircleInvitationEdgesUnsafe(PlayerRecord player)
    {
        foreach (var inviter in _playersById.Values)
            inviter.OutgoingCircleInvitationPlayerIds.Remove(player.PlayerId);

        player.IncomingCircleInviterPlayerIds.Clear();
    }

    public void UpdateDisplayName(PlayerRecord player, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return;

        player.DisplayName = displayName.Trim();
        player.TouchProfile();
        logger.LogInformation("Updated player {PlayerId} display name to {DisplayName}", player.PlayerId, player.DisplayName);
    }

    public void RegisterConnection(PlayerRecord player, string password)
    {
        lock (player.ConnectionStateLock)
            player.ConnectionPassword = password;
    }

    public bool TryGetConnectedPlayer(string playerId, string password, out PlayerRecord player)
    {
        if (!_playersById.TryGetValue(playerId, out player!))
            return false;

        lock (player.ConnectionStateLock)
            return player.ConnectionPassword.Length != 0 &&
                   string.Equals(player.ConnectionPassword, password, StringComparison.Ordinal);
    }

    public void Remove(PlayerRecord player)
    {
        lock (_friendStateLock)
        {
            lock (_invitationStateLock)
            {
                lock (_circleStateLock)
                {
                    var joinedCircleId = player.CircleId;
                    var ownedCircleId = player.OwnedCircle?.Id ?? 0;
                    ClearCircleSubmasterUnsafe(player.PlayerId);
                    if (ownedCircleId != 0)
                    {
                        foreach (var member in _playersById.Values.Where(candidate => candidate.CircleId == ownedCircleId))
                            member.CircleId = 0;
                    }

                    foreach (var other in _playersById.Values)
                    {
                        other.InvitationEstablishments.Remove(player.PlayerId);
                        other.NewInvitationPlayerIds.Remove(player.PlayerId);
                        other.AcceptedFriendPlayerIds.Remove(player.PlayerId);
                        other.PendingSentFriendPlayerIds.Remove(player.PlayerId);
                        other.ReceivedFriendPlayerIds.Remove(player.PlayerId);
                        other.PendingCircleApplicantIds.Remove(player.PlayerId);
                        other.OutgoingCircleInvitationPlayerIds.Remove(player.PlayerId);
                        other.IncomingCircleInviterPlayerIds.Remove(player.PlayerId);
                    }

                    player.PendingCircleApplicantIds.Clear();
                    player.OutgoingCircleInvitationPlayerIds.Clear();
                    player.IncomingCircleInviterPlayerIds.Clear();
                    player.CircleSubmasterPlayerIds.Clear();
                    player.CircleId = 0;
                    player.OwnedCircle = null;
                    if (ownedCircleId == 0 && joinedCircleId != 0)
                    {
                        var master = _playersById.Values.FirstOrDefault(candidate => candidate.OwnedCircle?.Id == joinedCircleId);
                        if (master != null)
                            UpdateCircleMemberCountUnsafe(master);
                    }
                    _playersByAuthorization.TryRemove(player.AuthorizationKey, out _);
                    _playersByProfileId.TryRemove(player.ProfileId, out _);
                    _playersById.TryRemove(player.PlayerId, out _);
                }
            }
        }

        logger.LogInformation("Removed player {PlayerId} profile {ProfileId}", player.PlayerId, player.ProfileId);
    }

    public void UpdateLiveSetting(PlayerRecord player, byte[] settingAll)
    {
        player.LiveSettingAll = LiveSettingCodec.NormalizeLiveSettingAll(settingAll);
        logger.LogInformation("Updated player {PlayerId} live setting ({ByteCount} bytes)", player.PlayerId, settingAll.Length);
    }

    public void UpdateFavoriteMember(PlayerRecord player, long memberCardId)
    {
        player.FavoriteMemberCardId = memberCardId;
        player.TouchProfile();
        logger.LogInformation(
            "Updated player {PlayerId} favorite member card to {MemberCardId}",
            player.PlayerId,
            memberCardId);
    }

    public void SaveDecks(PlayerRecord player, IEnumerable<Deck> decks, int mainDeck)
    {
        var deckPatches = decks.Select(CloneDeckState).ToArray();

        lock (player.DeckStateLock)
        {
            foreach (var deck in deckPatches)
                player.DeckOverrides[deck.Id] = deck;

            if (mainDeck != 0)
                player.MainDeckOverride = mainDeck;

            if (player.FavoriteMemberCardId == 0)
                player.TouchProfile();
        }

        logger.LogInformation(
            "Updated player {PlayerId} decks ({DeckCount} patches, main deck {MainDeck})",
            player.PlayerId,
            deckPatches.Length,
            mainDeck);
    }

    public void ReadStoryEpisode(PlayerRecord player, long episodeId, bool isSkipped)
    {
        var episode = new StoryEpisode
        {
            EpisodeId = episodeId,
            IsSkipped = isSkipped,
            LastReadAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        lock (player.StoryStateLock)
            player.SeenStoryEpisodes[episodeId] = episode;

        logger.LogInformation(
            "Updated player {PlayerId} story episode {EpisodeId} (skipped: {IsSkipped})",
            player.PlayerId,
            episodeId,
            isSkipped);
    }

    public void ReadFriendshipEpisode(PlayerRecord player, long episodeId, bool isSkipped)
    {
        var episode = new StoryEpisode
        {
            EpisodeId = episodeId,
            IsSkipped = isSkipped,
            LastReadAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        lock (player.StoryStateLock)
            player.SeenStoryFriendshipEpisodes[episodeId] = episode;

        logger.LogInformation(
            "Updated player {PlayerId} friendship story episode {EpisodeId} (skipped: {IsSkipped})",
            player.PlayerId,
            episodeId,
            isSkipped);
    }

    public void SaveBirth(PlayerRecord player, int year, int month)
    {
        var now = DateTimeOffset.UtcNow;
        var isFutureMonth = year > now.Year || (year == now.Year && month > now.Month);
        if (year < 1900 || month is < 1 or > 12 || isFutureMonth)
        {
            logger.LogWarning(
                "Ignored invalid birth month {Year}-{Month} for player {PlayerId}",
                year,
                month,
                player.PlayerId);
            return;
        }

        lock (player.ShopStateLock)
        {
            player.ShopBirthYear = year;
            player.ShopBirthMonth = month;
        }

        logger.LogInformation(
            "Updated player {PlayerId} birth month to {Year}-{Month}",
            player.PlayerId,
            year,
            month);
    }

    public (int Year, int Month) GetShopBirth(PlayerRecord player)
    {
        lock (player.ShopStateLock)
            return (player.ShopBirthYear, player.ShopBirthMonth);
    }

    public void SaveStampFavorites(PlayerRecord player, IEnumerable<UpdateStampFavorite> favorites)
    {
        var patches = new Dictionary<int, long[]>();
        var oversizedGroupCount = 0;
        var negativeStampIdCount = 0;
        var invalidFavoriteIdCount = 0;
        var duplicateGroupCount = 0;

        foreach (var favorite in favorites)
        {
            if (favorite.FavoriteId is < 0 or >= FavoriteStampGroupCount)
            {
                invalidFavoriteIdCount++;
                continue;
            }

            var stampIds = new long[FavoriteStampSlotCount];
            var count = Math.Min(favorite.StampIds.Count, FavoriteStampSlotCount);
            for (var index = 0; index < count; index++)
            {
                var stampId = favorite.StampIds[index];
                if (stampId > 0)
                    stampIds[index] = stampId;
                else if (stampId < 0)
                    negativeStampIdCount++;
            }

            if (favorite.StampIds.Count > FavoriteStampSlotCount)
                oversizedGroupCount++;

            if (!patches.TryAdd(favorite.FavoriteId, stampIds))
            {
                patches[favorite.FavoriteId] = stampIds;
                duplicateGroupCount++;
            }
        }

        var unknownStampIdCount = 0;
        lock (player.StampStateLock)
        {
            foreach (var (favoriteId, stampIds) in patches)
            {
                for (var index = 0; index < stampIds.Length; index++)
                {
                    if (stampIds[index] > 0 && !player.OwnedStampIds.Contains(stampIds[index]))
                    {
                        stampIds[index] = 0;
                        unknownStampIdCount++;
                    }
                }

                player.StampFavoriteGroups[favoriteId] = stampIds;
            }
        }

        logger.LogInformation(
            "Updated player {PlayerId} stamp favorites ({GroupCount} groups)",
            player.PlayerId,
            patches.Count);

        var wasNormalized = oversizedGroupCount > 0 ||
            negativeStampIdCount > 0 ||
            invalidFavoriteIdCount > 0 ||
            duplicateGroupCount > 0 ||
            unknownStampIdCount > 0;
        if (wasNormalized)
        {
            logger.LogWarning(
                "Normalized stamp favorites for player {PlayerId} " +
                "({OversizedGroupCount} oversized groups, {NegativeStampIdCount} negative stamp ids, " +
                "{InvalidFavoriteIdCount} invalid favorite ids, {DuplicateGroupCount} duplicate groups, " +
                "{UnknownStampIdCount} unknown stamp ids)",
                player.PlayerId,
                oversizedGroupCount,
                negativeStampIdCount,
                invalidFavoriteIdCount,
                duplicateGroupCount,
                unknownStampIdCount);
        }
    }

    public void SaveStampFavoriteName(PlayerRecord player, int favoriteId, string name)
    {
        if (favoriteId is < 0 or >= FavoriteStampGroupCount || name.Length > FavoriteStampNameMaxLength)
        {
            logger.LogWarning(
                "Ignored invalid stamp favorite name for player {PlayerId} (favorite {FavoriteId}, length {NameLength})",
                player.PlayerId,
                favoriteId,
                name.Length);
            return;
        }

        lock (player.StampStateLock)
        {
            if (name.Length == 0)
                player.StampFavoriteNames.Remove(favoriteId);
            else
                player.StampFavoriteNames[favoriteId] = name;
        }

        logger.LogInformation(
            "Updated player {PlayerId} stamp favorite {FavoriteId} name",
            player.PlayerId,
            favoriteId);
    }

    public void SaveShownCarouselHelps(PlayerRecord player, IEnumerable<long> masterIds)
    {
        foreach (var masterId in masterIds)
            player.ShownCarouselHelpIds.TryAdd(masterId, 0);

        logger.LogInformation(
            "Updated player {PlayerId} shown carousel helps ({Count} ids)",
            player.PlayerId,
            player.ShownCarouselHelpIds.Count);
    }

    public void SaveShownContentUnlocks(PlayerRecord player, IEnumerable<long> masterIds)
    {
        foreach (var masterId in masterIds)
            player.ShownContentUnlockIds.TryAdd(masterId, 0);

        logger.LogInformation(
            "Updated player {PlayerId} shown content unlocks ({Count} ids)",
            player.PlayerId,
            player.ShownContentUnlockIds.Count);
    }

    public bool IsKnownCredential(HttpRequest request) => TryGetFromRequest(request, out _);

    private bool TryGetFromRequest(HttpRequest request, out PlayerRecord player)
    {
        player = null!;
        if (request.Headers.TryGetValue("x-player-credential", out var credentials))
        {
            if (credentials.Count != 1 || string.IsNullOrEmpty(credentials[0]) ||
                !request.Headers.TryGetValue("x-player-id", out var ids) || ids.Count != 1 ||
                !_playersByAuthorization.TryGetValue(credentials[0]!, out var authenticated) ||
                !string.Equals(authenticated.PlayerId, ids[0], StringComparison.Ordinal))
                return false;

            if (request.Headers.TryGetValue("authorization", out var suppliedAuthorization) &&
                (suppliedAuthorization.Count != 1 || string.IsNullOrWhiteSpace(suppliedAuthorization[0]) ||
                 (!ServerConfig.IsExpectedBasicAuth(suppliedAuthorization[0]!) &&
                  !string.Equals(StripBearerPrefix(suppliedAuthorization[0]!), authenticated.AuthorizationKey,
                      StringComparison.Ordinal))))
                return false;

            player = authenticated;
            return true;
        }

        if (!request.Headers.TryGetValue("authorization", out var values) || values.Count != 1)
            return false;

        var authorization = values[0];
        if (string.IsNullOrWhiteSpace(authorization))
            return false;

        if (!ServerConfig.IsExpectedBasicAuth(authorization))
            return _playersByAuthorization.TryGetValue(StripBearerPrefix(authorization), out player!);

        var playerId = ReadHeader(request, "x-player-id")
            ?? ReadHeader(request, "player-id")
            ?? ReadHeader(request, "playerid")
            ?? ReadHeader(request, "x-playerid");

        if (playerId != null && _playersById.TryGetValue(playerId, out var matched))
        {
            player = matched;
            return true;
        }

        return false;
    }

    private void Add(PlayerRecord player)
    {
        _playersById[player.PlayerId] = player;
        _playersByProfileId[player.ProfileId] = player;
        _playersByAuthorization[player.AuthorizationKey] = player;
    }

    private PlayerRecord[] ResolvePlayers(IEnumerable<string> playerIds) =>
        playerIds
            .Select(playerId => _playersById.TryGetValue(playerId, out var player) ? player : null)
            .OfType<PlayerRecord>()
            .OrderBy(player => player.ProfileId)
            .ToArray();

    private static string? ReadHeader(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var value) && value.Count > 0 ? value.ToString() : null;

    private static string StripBearerPrefix(string value) =>
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;

    private static Deck CloneDeckState(Deck source)
    {
        var result = new Deck
        {
            Id = source.Id,
            Name = source.Name
        };

        foreach (var card in source.Cards)
            result.Cards.Add(card.Clone());

        return result;
    }

    private static string NewToken(int bytes)
    {
        Span<byte> data = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(data);
        return Convert.ToHexString(data).ToLowerInvariant();
    }
}
