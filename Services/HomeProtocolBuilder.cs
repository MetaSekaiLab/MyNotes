using App.Protobuf.Entity;
using App.Protobuf.Home;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class HomeProtocolBuilder(PlayerManager players, PlayerProtocolBuilder playerProtocol)
{
    public GetHomeResponse Get(HomeSnapshot snapshot, PlayerRecord player)
    {
        var response = new GetHomeResponse
        {
            Notification = snapshot.HasNotification ? new Notification() : null,
            Friends = snapshot.HasFriends ? playerProtocol.BuildFriends(player, players) : null
        };
        ClientStateProtocol.InitializeHome(response);

        foreach (var invitation in players.GetInvitationProfiles(player))
        {
            response.Invitations.Add(new InvitationProfile
            {
                PlayerProfile = playerProtocol.BuildSimpleProfile(invitation.Player),
                EstablishmentAt = invitation.EstablishmentAt
            });
        }

        return response;
    }
}
