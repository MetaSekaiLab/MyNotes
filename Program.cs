using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using MyNotes.Controllers;
using MyNotes.Config;
using MyNotes.Middleware;
using MyNotes.Services;
using Protocol = App.Protobuf;

if (args.FirstOrDefault() == "resource-release")
{
    if (args.Length != 7)
        throw new ArgumentException("usage: resource-release BASELINE PACKAGE MASTER_JSON CATALOG_NAME RESOURCE_VERSION OUTPUT");
    Console.WriteLine(ResourceReleaseBuilder.Build(args[1], args[2], args[3], args[4], args[5], args[6]));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var configIndex = Array.IndexOf(args, "--config");
if (configIndex >= 0)
{
    if (configIndex + 1 >= args.Length)
        throw new ArgumentException("--config requires a JSON file path");
    builder.Configuration.AddJsonFile(Path.GetFullPath(args[configIndex + 1]), optional: false, reloadOnChange: false)
        .AddEnvironmentVariables().AddCommandLine(args);
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

ServerConfig.Load(builder.Configuration);
X509Certificate2? serverCertificate = null;
X509Certificate2Collection? serverCertificateChain = null;
SslStreamCertificateContext? serverCertificateContext = null;
if (!string.IsNullOrWhiteSpace(ServerConfig.CertificatePath))
{
    var certificates = X509CertificateLoader.LoadPkcs12CollectionFromFile(
        Path.GetFullPath(ServerConfig.CertificatePath, builder.Environment.ContentRootPath),
        ServerConfig.CertificatePassword);
    serverCertificate = certificates.Cast<X509Certificate2>().First(certificate => certificate.HasPrivateKey);
    serverCertificateChain = new X509Certificate2Collection();
    foreach (var certificate in certificates)
        if (!certificate.Equals(serverCertificate)) serverCertificateChain.Add(certificate);
    var roots = new X509Certificate2Collection();
    foreach (var certificate in serverCertificateChain)
        if (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
            roots.Add(certificate);
    serverCertificateContext = roots.Count == 0
        ? SslStreamCertificateContext.Create(serverCertificate, serverCertificateChain, offline: true)
        : SslStreamCertificateContext.Create(serverCertificate, serverCertificateChain, offline: true,
            SslCertificateTrust.CreateForX509Collection(roots, sendTrustInHandshake: false));
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(ServerConfig.GrpcPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http2;
        if (serverCertificate != null)
            listen.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate;
                https.ServerCertificateChain = serverCertificateChain;
                https.OnAuthenticate = (_, options) => options.ServerCertificateContext = serverCertificateContext;
            });
    });

    options.ListenAnyIP(ServerConfig.HttpPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http1;
        if (serverCertificate != null)
            listen.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate;
                https.ServerCertificateChain = serverCertificateChain;
                https.OnAuthenticate = (_, options) => options.ServerCertificateContext = serverCertificateContext;
            });
    });
});

builder.Services.AddHttpClient("official-assets", client =>
{
    client.Timeout = TimeSpan.FromSeconds(ServerConfig.OfficialAssetRequestTimeoutSeconds);
});

builder.Services.AddSingleton<PlayerManager>();
builder.Services.AddSingleton<ResourceReleaseService>();
builder.Services.AddSingleton<MasterDataService>();
builder.Services.AddSingleton<HomeSnapshotService>();
builder.Services.AddSingleton<StaticAssetService>();
builder.Services.AddSingleton<MasterdataProtocolBuilder>();
builder.Services.AddSingleton<PlayerProtocolBuilder>();
builder.Services.AddSingleton<HomeProtocolBuilder>();
builder.Services.AddSingleton<LiveProtocolBuilder>();
builder.Services.AddSingleton<FreeLiveService>();
builder.Services.AddSingleton<PlayerLoginService>();
builder.Services.AddSingleton<FeatureSwitchService>();

var app = builder.Build();
_ = app.Services.GetRequiredService<MasterDataService>();
_ = app.Services.GetRequiredService<PlayerLoginService>();
_ = app.Services.GetRequiredService<FeatureSwitchService>();
var appLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MyNotes");

app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await next();
    sw.Stop();

    appLogger.LogInformation("{Method} {Path} -> {StatusCode} ({Elapsed}ms)",
        ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
});

app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    if (feature != null)
        appLogger.LogError(feature.Error, "Unhandled request error");

    if (GrpcFraming.IsGrpcRequest(ctx.Request))
    {
        await GrpcFraming.WriteErrorAsync(ctx, "13", "internal server error");
        return;
    }

    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { error = "Internal Server Error" });
}));

app.UseRouting();
app.UseMiddleware<RequestLoggingScopeMiddleware>();

app.MapGrpcUnary("/app.playerlogin.PlayerLoginService/GetServerList",
    (ctx, payload) =>
    {
        _ = Empty.Parser.ParseFrom(payload.ToArray());
        return Task.FromResult(ctx.RequestServices.GetRequiredService<PlayerLoginService>().ServerList());
    });
app.MapGrpcUnary("/app.playerlogin.PlayerLoginService/PlayerPreLogin",
    (ctx, payload) => Task.FromResult(ctx.RequestServices.GetRequiredService<PlayerLoginService>().PreLogin(payload)));
app.MapGrpcUnary("/app.playerlogin.PlayerLoginService/PlayerLogin",
    (ctx, payload) => Task.FromResult(ctx.RequestServices.GetRequiredService<PlayerLoginService>().Login(payload)));

app.MapGrpcUnary("/app.feature_switch.FeatureSwitchService/Get",
    (ctx, payload) =>
    {
        _ = Empty.Parser.ParseFrom(payload.ToArray());
        _ = ctx.RequestServices.GetRequiredService<PlayerManager>().GetFromRequest(ctx.Request);
        return Task.FromResult(ctx.RequestServices.GetRequiredService<FeatureSwitchService>().Get());
    });

app.MapGrpcUnary(
    "/app.masterdata.MasterdataService/Version",
    Protocol.Masterdata.VersionRequest.Parser,
    (ctx, _) =>
    {
        var protocol = ctx.RequestServices.GetRequiredService<MasterdataProtocolBuilder>();
        return Task.FromResult(protocol.Version());
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/Register",
    Protocol.Player.RegisterRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var player = players.Register(request.InitialDataGroup);
        return Task.FromResult(protocol.Register(player));
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/Whoami",
    Protocol.Player.WhoamiRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(new Protocol.Player.WhoamiResponse { PlayerId = player.PlayerId });
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/RegisterDevice",
    Protocol.Player.RegisterDeviceRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(protocol.RegisterDevice(player));
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/RegisterConnection",
    Protocol.Player.RegisterConnectionRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.RegisterConnection(player, request.Password);
        return Task.FromResult(new Protocol.Player.RegisterConnectionResponse());
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/GetConnectedPlayer",
    Protocol.Player.GetConnectedPlayerRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var response = new Protocol.Player.GetConnectedPlayerResponse();
        if (players.TryGetConnectedPlayer(request.PlayerId, request.Password, out var player))
        {
            response.Nickname = player.DisplayName;
            response.PlayerRank = 1;
        }

        return Task.FromResult(response);
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/RegisterDeviceForConnectedPlayer",
    Protocol.Player.RegisterDeviceForConnectedPlayerRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        return Task.FromResult(
            players.TryGetConnectedPlayer(request.PlayerId, request.Password, out var player)
                ? protocol.RegisterDeviceForConnectedPlayer(player)
                : new Protocol.Player.RegisterDeviceForConnectedPlayerResponse());
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/RemovePlayerData",
    Protocol.Player.RemovePlayerDataRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        players.Remove(players.GetFromRequest(ctx.Request));
        return Task.FromResult(new Protocol.Player.RemovePlayerDataResponse());
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/GetPlayerData",
    Protocol.Player.GetPlayerDataRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(protocol.GetPlayerData(player, players));
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/CheckNgWord",
    Protocol.Player.CheckNgWordRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Player.CheckNgWordResponse()));

app.MapGrpcUnary(
    "/app.player.PlayerService/EditProfile",
    Protocol.Player.EditProfileRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.UpdateDisplayName(player, request.Name);
        return Task.FromResult(new Protocol.Player.EditProfileResponse());
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/ChangeFavoriteMember",
    Protocol.Player.ChangeFavoriteMemberRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.UpdateFavoriteMember(player, request.MemberId);
        return Task.FromResult(new Protocol.Player.ChangeFavoriteMemberResponse());
    });

app.MapGrpcUnary(
    "/app.friend.FriendService/FindByProfileID",
    Protocol.Friend.FindByProfileIDRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var response = new Protocol.Friend.FindByProfileIDResponse();
        if (players.TryGetByProfileId(request.PlayerProfileId, out var player))
            response.PlayerProfile = protocol.BuildSimpleProfile(player);

        return Task.FromResult(response);
    });

app.MapGrpcUnary(
    "/app.friend.FriendService/FriendRequest",
    Protocol.Friend.FriendApprovalRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        var result = players.RequestFriend(player, request.TargetPlayerId);
        return Task.FromResult(new Protocol.Friend.FriendApprovalResponse
        {
            FriendPlayerProfile = result.Target == null ? null : protocol.BuildSimpleProfile(result.Target),
            IsAccepted = result.IsAccepted
        });
    });

app.MapGrpcUnary(
    "/app.friend.FriendService/FriendRequestWithdrawal",
    Protocol.Friend.FriendRequestWithdrawalRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.WithdrawFriendRequest(player, request.TargetPlayerId);
        return Task.FromResult(new Protocol.Friend.FriendRequestWithdrawalResponse());
    });

app.MapGrpcUnary(
    "/app.friend.FriendService/FriendAnswer",
    Protocol.Friend.FriendAnswerRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        var accepted = players.AnswerFriendRequests(player, request.TargetPlayerIds, request.Answer);
        var response = new Protocol.Friend.FriendAnswerResponse();
        response.FriendPlayerProfiles.Add(accepted.Select(protocol.BuildSimpleProfile));
        return Task.FromResult(response);
    });

app.MapGrpcUnary(
    "/app.friend.FriendService/FriendUnlink",
    Protocol.Friend.FriendUnlinkRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.UnlinkFriend(player, request.FriendPlayerId);
        return Task.FromResult(new Protocol.Friend.FriendUnlinkResponse());
    });

app.MapGrpcUnary(
    "/app.friend.FriendService/PlayerReport",
    Protocol.Friend.PlayerReportRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var reporter = players.GetFromRequest(ctx.Request);
        players.LogPlayerReport(
            reporter,
            request.TargetPlayerId,
            request.ReportType,
            request.ReportIds);
        return Task.FromResult(new Protocol.Friend.PlayerReportResponse());
    });

app.MapGrpcUnary(
    "/app.invitation.InvitationService/GenerateInvitationCode",
    Protocol.Invitation.GenerateInvitationCodeRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(new Protocol.Invitation.GenerateInvitationCodeResponse
        {
            InvitationCode = PlayerManager.GetInvitationCode(player)
        });
    });

app.MapGrpcUnary(
    "/app.invitation.InvitationService/InputInvitationCode",
    Protocol.Invitation.InputInvitationCodeRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(new Protocol.Invitation.InputInvitationCodeResponse
        {
            IsEstablishment = players.InputInvitationCode(player, request.InvitationCode)
        });
    });

app.MapGrpcUnary(
    "/app.invitation.InvitationService/UpdateInvitationView",
    Protocol.Invitation.UpdateInvitationViewRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        players.MarkInvitationsViewed(players.GetFromRequest(ctx.Request), request.TargetPlayerIds);
        return Task.FromResult(new Protocol.Invitation.UpdateInvitationViewResponse());
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/GetRecommendedCircleList",
    Protocol.Circle.GetRecommendedCircleListRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var profiles = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var response = new Protocol.Circle.GetRecommendedCircleListResponse();
        response.Circles.Add(players.GetRecommendedCircles(players.GetFromRequest(ctx.Request)).Select(item =>
            new App.Protobuf.Entity.CircleWithMasterPlayer
            {
                Circle = item.Circle,
                Profile = profiles.BuildSimpleProfile(item.Master)
            }));
        return Task.FromResult(response);
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/CreateCircle",
    Protocol.Circle.SaveCircleRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        var circleId = players.CreateCircle(player, request.Params ?? new App.Protobuf.Entity.SaveCircleParams());
        return Task.FromResult(new Protocol.Circle.CreateCircleResponse { CircleId = circleId });
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/EditCircle",
    Protocol.Circle.SaveCircleRequest.Parser,
    (ctx, request) =>
    {
        if (request.Params != null)
        {
            var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
            players.EditCircle(players.GetFromRequest(ctx.Request), request.Params);
        }

        return Task.FromResult(new Protocol.Circle.EditCircleResponse());
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/Search",
    Protocol.Circle.SearchRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var profiles = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var response = new Protocol.Circle.SearchResponse();
        response.Circles.Add(players.SearchCircles(players.GetFromRequest(ctx.Request), request.Options).Select(item =>
            new App.Protobuf.Entity.CircleWithMasterPlayer
            {
                Circle = item.Circle,
                Profile = profiles.BuildSimpleProfile(item.Master)
            }));
        return Task.FromResult(response);
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/GetCircleDetail",
    Protocol.Circle.GetCircleRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var circle = players.GetCircleSnapshot(request.CircleId);
        if (circle == null)
            return Task.FromResult(new Protocol.Circle.GetCircleResponse());

        var profiles = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var detail = new App.Protobuf.Entity.CircleDetailWithPlayerList
        {
            Detail = circle.Circle
        };
        detail.Players.Add(circle.Members.Select(member => profiles.BuildCirclePlayer(
            member,
            circle.Circle.Id,
            circle.GetAuth(member))));
        return Task.FromResult(new Protocol.Circle.GetCircleResponse { Circle = detail });
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/GetCircle",
    Protocol.Circle.GetCircleSettingRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        var circle = players.GetCircleSnapshot(player.CircleId);
        if (circle == null)
            return Task.FromResult(new Protocol.Circle.GetCircleSettingResponse());

        var profiles = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        return Task.FromResult(new Protocol.Circle.GetCircleSettingResponse
        {
            Circle = circle.Circle,
            CirclePlayer = profiles.BuildCirclePlayer(
                player,
                circle.Circle.Id,
                circle.GetAuth(player))
        });
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/DeleteCircle",
    Empty.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        players.DeleteCircle(players.GetFromRequest(ctx.Request));
        return Task.FromResult(new Empty());
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/ExitCircle",
    Protocol.Circle.ExitCircleRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        players.ExitCircle(players.GetFromRequest(ctx.Request));
        return Task.FromResult(new Protocol.Circle.ExitCircleResponse());
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/TransferMaster",
    Protocol.Circle.ChangeAuthRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        players.TransferCircleMaster(players.GetFromRequest(ctx.Request), request.PlayerId);
        return Task.FromResult(new Protocol.Circle.ChangeAuthResponse());
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/TransferSubmaster",
    Protocol.Circle.ChangeAuthRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        players.TransferCircleSubmaster(players.GetFromRequest(ctx.Request), request.PlayerId);
        return Task.FromResult(new Protocol.Circle.ChangeAuthResponse());
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/SetSubmaster",
    Protocol.Circle.ChangeAuthRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        players.SetCircleSubmaster(players.GetFromRequest(ctx.Request), request.PlayerId);
        return Task.FromResult(new Protocol.Circle.ChangeAuthResponse());
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/UnsetSubmaster",
    Protocol.Circle.ChangeAuthRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        players.UnsetCircleSubmaster(players.GetFromRequest(ctx.Request), request.PlayerId);
        return Task.FromResult(new Protocol.Circle.ChangeAuthResponse());
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/RemovePlayer",
    Protocol.Circle.RemovePlayerRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        players.RemoveCirclePlayer(players.GetFromRequest(ctx.Request), request.PlayerId);
        return Task.FromResult(new Protocol.Circle.RemovePlayerResponse());
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/UpdateCircleTop",
    Empty.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        var circle = players.GetCircleSnapshot(player.CircleId);
        if (circle == null)
            return Task.FromResult(new Protocol.Circle.UpdateCircleTopResponse());

        var profiles = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var circlePlayer = profiles.BuildCirclePlayer(
            player,
            circle.Circle.Id,
            circle.GetAuth(player));
        var top = new App.Protobuf.Entity.UpdateCircleTop
        {
            Circle = circle.Circle,
            CurrentPlayer = circlePlayer
        };
        top.Players.Add(circle.Members.Select(member => profiles.BuildCirclePlayer(
            member,
            circle.Circle.Id,
            circle.GetAuth(member))));
        return Task.FromResult(new Protocol.Circle.UpdateCircleTopResponse { CircleTop = top });
    });

app.MapGrpcUnary(
    "/app.circleinvitation.CircleInvitationService/GetInvitationList",
    Protocol.CircleInvitation.GetInvitationListRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var profiles = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var response = new Protocol.CircleInvitation.GetInvitationListResponse();
        response.Invitations.Add(players.GetIncomingCircleInvitations(players.GetFromRequest(ctx.Request)).Select(item =>
            new App.Protobuf.Entity.CircleInvitation
            {
                CircleId = item.Circle.Id,
                PlayerId = item.Inviter.PlayerId,
                CircleDetail = item.Circle,
                Profile = profiles.BuildSimpleProfile(item.Inviter)
            }));
        return Task.FromResult(response);
    });

app.MapGrpcUnary(
    "/app.circleinvitation.CircleInvitationService/GetInvitingPlayer",
    Empty.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var profiles = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var response = new Protocol.CircleInvitation.GetInvitingPlayerResponse();
        response.PlayerBasicProfiles.Add(players.GetInvitedCirclePlayers(players.GetFromRequest(ctx.Request)).Select(profiles.BuildSimpleProfile));
        return Task.FromResult(response);
    });

app.MapGrpcUnary(
    "/app.circleinvitation.CircleInvitationService/InviteCircle",
    Protocol.CircleInvitation.InviteCircleRequest.Parser,
    (ctx, request) =>
    {
        if (request.HasPlayerId)
        {
            var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
            players.InviteCirclePlayer(players.GetFromRequest(ctx.Request), request.PlayerId);
        }
        return Task.FromResult(new Empty());
    });

app.MapGrpcUnary(
    "/app.circleinvitation.CircleInvitationService/RevokeCircleInvitation",
    Protocol.CircleInvitation.RevokeCircleInvitationRequest.Parser,
    (ctx, request) =>
    {
        if (request.HasPlayerId)
        {
            var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
            players.RevokeCircleInvitation(players.GetFromRequest(ctx.Request), request.PlayerId);
        }
        return Task.FromResult(new Empty());
    });

app.MapGrpcUnary(
    "/app.circleinvitation.CircleInvitationService/GetCircleInvitationRecommendList",
    Empty.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var profiles = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var response = new Protocol.CircleInvitation.GetCircleInvitationRecommendListResponse();
        response.PlayerBasicProfiles.Add(players.GetCircleInvitationCandidates(players.GetFromRequest(ctx.Request)).Select(profiles.BuildSimpleProfile));
        return Task.FromResult(response);
    });

app.MapGrpcUnary(
    "/app.circleinvitation.CircleInvitationService/GetInvitablePlayer",
    Protocol.CircleInvitation.GetInvitablePlayerRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var target = request.HasPlayerId
            ? players.GetInvitableCirclePlayer(players.GetFromRequest(ctx.Request), request.PlayerId)
            : null;
        var response = new Protocol.CircleInvitation.GetInvitablePlayerResponse();
        if (target != null)
        {
            var profiles = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
            response.PlayerBasicProfile = profiles.BuildSimpleProfile(target);
        }
        return Task.FromResult(response);
    });

app.MapGrpcUnary(
    "/app.circlejoinrequest.CircleJoinReqService/CircleJoinReq",
    Protocol.CircleJoinRequest.CircleJoinReqRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        var circleId = request.HasCircleId ? request.CircleId : 0;
        var outcome = players.JoinCircle(player, circleId);
        if (outcome is PlayerManager.CircleJoinOutcome.MissingCircle or PlayerManager.CircleJoinOutcome.Rejected)
            throw new GrpcStatusException("9", "circle join request is not valid");

        return Task.FromResult(new Protocol.CircleJoinRequest.CircleJoinReqResponse());
    });

app.MapGrpcUnary(
    "/app.circlejoinrequest.CircleJoinReqService/CircleJoinApprove",
    Protocol.CircleJoinRequest.CircleJoinApproveRequest.Parser,
    (ctx, request) =>
    {
        if (request.HasPlayerId)
        {
            var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
            players.ApproveCircleJoinRequest(players.GetFromRequest(ctx.Request), request.PlayerId);
        }
        return Task.FromResult(new Protocol.CircleJoinRequest.CircleJoinApproveResponse());
    });

app.MapGrpcUnary(
    "/app.circlejoinrequest.CircleJoinReqService/CircleJoinRevoke",
    Protocol.CircleJoinRequest.CircleJoinRevokeRequest.Parser,
    (ctx, request) =>
    {
        if (request.HasPlayerId)
        {
            var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
            players.RevokeCircleJoinRequest(players.GetFromRequest(ctx.Request), request.PlayerId);
        }
        return Task.FromResult(new Protocol.CircleJoinRequest.CircleJoinRevokeResponse());
    });

app.MapGrpcUnary(
    "/app.circlejoinrequest.CircleJoinReqService/GetCircleJoinRequestList",
    Empty.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var profiles = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var response = new Protocol.CircleJoinRequest.GetCircleJoinRequestListResponse();
        response.CircleJoinRequests.Add(players.GetCircleJoinRequests(players.GetFromRequest(ctx.Request)).Select(applicant =>
            new App.Protobuf.Entity.CircleJoinRequest
            {
                PlayerId = applicant.PlayerId,
                Profile = profiles.BuildSimpleProfile(applicant)
            }));
        return Task.FromResult(response);
    });

app.MapGrpcUnary(
    "/app.circleranking.CircleRankingService/GetCircleRanking",
    Empty.Parser,
    static (_, _) => Task.FromResult(new Protocol.CircleRanking.GetCircleRankingResponse()));

app.MapGrpcUnary(
    "/app.circlerankupreward.CircleRankUpRewardService/GetCircleRankUpRewardReceived",
    Empty.Parser,
    static (_, _) => Task.FromResult(new Protocol.CircleRankUpReward.GetCircleRankUpRewardReceivedResponse()));

app.MapGrpcUnary(
    "/app.circlemission.CircleMissionService/Fetch",
    Empty.Parser,
    static (_, _) => Task.FromResult(new Protocol.CircleMission.FetchCircleMissionResponse()));

app.MapGrpcUnary(
    "/app.circleplayer.CirclePlayerService/AutoTransferMaster",
    Empty.Parser,
    static (_, _) => Task.FromResult(new Protocol.CirclePlayer.AutoTransferMasterResponse()));

app.MapGrpcUnary(
    "/app.circleplayer.CirclePlayerService/GetPlayerAuth",
    Empty.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(new Protocol.CirclePlayer.GetPlayerAuthResponse
        {
            Auth = players.GetCircleAuth(player)
        });
    });

app.MapGrpcUnary(
    "/app.deck.DeckService/SaveDecks",
    Protocol.Deck.SaveDecksRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveDecks(player, request.Decks, request.MainDeck);
        return Task.FromResult(new Protocol.Deck.SaveDecksResponse());
    });

app.MapGrpcUnary(
    "/app.home.HomeService/Get",
    Protocol.Home.GetHomeRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var home = ctx.RequestServices.GetRequiredService<HomeSnapshotService>();
        var protocol = ctx.RequestServices.GetRequiredService<HomeProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(protocol.Get(home.GetFor(player), player));
    });

app.MapGrpcUnary(
    "/app.present.PresentService/Fetch",
    Protocol.Present.FetchRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Present.FetchResponse()));

app.MapGrpcUnary(
    "/app.present.PresentService/Open",
    Protocol.Present.OpenRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Present.OpenResponse()));

app.MapGrpcUnary(
    "/app.present.PresentService/History",
    Protocol.Present.HistoryRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Present.HistoryResponse()));

app.MapGrpcUnary(
    "/app.mission.MissionService/Fetch",
    Protocol.Present.FetchMissionRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Present.FetchMissionResponse()));

app.MapGrpcUnary(
    "/app.loginbonus.LoginBonusService/Update",
    Protocol.Present.UpdateLoginbonusRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Present.UpdateLoginbonusResponse()));

app.MapGrpcUnary(
    "/app.announcement.AnnouncementService/GetList",
    Protocol.Announcement.GetListRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Announcement.GetListResponse()));

app.MapGrpcUnary(
    "/app.announcement.AnnouncementService/Get",
    Protocol.Announcement.GetRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Announcement.GetResponse()));

app.MapGrpcUnary(
    "/app.seasonpass.SeasonPassService/Fetch",
    Protocol.SeasonPass.FetchRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.SeasonPass.FetchResponse()));

app.MapGrpcUnary(
    "/app.playerext.PlayerExtService/SyncData",
    Protocol.PlayerExt.SyncDataRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(protocol.SyncData(player));
    });

app.MapGrpcUnary(
    "/app.chat.ClientChatService/SyncMsg",
    Protocol.Chat.ClientSyncMsgRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Chat.ClientSyncMsgResponse()));

app.MapGrpcUnary(
    "/app.chat.ClientChatService/GetNotifications",
    Protocol.Chat.GetNotificationsRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Chat.GetNotificationsResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/FetchBattleLiveInvitation",
    Protocol.Live.FetchBattleLiveInvitationRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Live.FetchBattleLiveInvitationResponse()));

app.MapGrpcUnary(
    "/app.carousel_help.CarouselHelpService/Shown",
    Protocol.CarouselHelp.ShownRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveShownCarouselHelps(player, request.MasterIds);
        return Task.FromResult(new Protocol.CarouselHelp.ShownResponse());
    });

app.MapGrpcUnary(
    "/app.content_unlock.ContentUnlockService/Shown",
    Protocol.ContentUnlock.ShownRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveShownContentUnlocks(player, request.MasterIds);
        return Task.FromResult(new Protocol.ContentUnlock.ShownResponse());
    });

app.MapGrpcUnary(
    "/app.stamp.StampService/UpdateStampFavorites",
    Protocol.Stamp.UpdateStampFavoritesRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveStampFavorites(player, request.Favorites);
        return Task.FromResult(new Protocol.Stamp.UpdateStampFavoritesResponse());
    });

app.MapGrpcUnary(
    "/app.stamp.StampService/UpdateStampFavoriteName",
    Protocol.Stamp.UpdateStampFavoriteNameRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveStampFavoriteName(player, request.FavoriteId, request.Name);
        return Task.FromResult(new Protocol.Stamp.UpdateStampFavoriteNameResponse());
    });

app.MapGrpcUnary(
    "/app.event.EventService/GetRankingList",
    Protocol.Event.GetRankingListRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Event.GetRankingListResponse()));

app.MapGrpcUnary(
    "/app.event.EventService/GetRank",
    Protocol.Event.GetRankRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Event.GetRankResponse()));

app.MapGrpcUnary(
    "/app.gacha.GachaService/History",
    Protocol.Gacha.HistoryRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Gacha.HistoryResponse()));

app.MapGrpcUnary(
    "/app.gacha.GachaService/CheckMaintenanceGacha",
    Protocol.Gacha.CheckMaintenanceGachaRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Gacha.CheckMaintenanceGachaResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/StartFree",
    Protocol.Live.StartFreeRequest.Parser,
    (ctx, request) => Task.FromResult(ctx.RequestServices.GetRequiredService<FreeLiveService>()
        .Start(ctx.RequestServices.GetRequiredService<PlayerManager>().GetFromRequest(ctx.Request), request)));

app.MapGrpcUnary(
    "/app.live.LiveService/FinishFree",
    Protocol.Live.FinishFreeRequest.Parser,
    (ctx, request) => Task.FromResult(ctx.RequestServices.GetRequiredService<FreeLiveService>()
        .Finish(ctx.RequestServices.GetRequiredService<PlayerManager>().GetFromRequest(ctx.Request), request)));

app.MapGrpcUnary<Protocol.Live.SkipFreeRequest, Protocol.Live.SkipFreeResponse>(
    "/app.live.LiveService/SkipFree",
    Protocol.Live.SkipFreeRequest.Parser,
    static (_, _) => throw new GrpcStatusException("12", "live skipping is not supported"));

app.MapGrpcUnary(
    "/app.live.LiveService/CheckMaintenanceCasualMatch",
    Protocol.Live.CheckMaintenanceCasualMatchRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Live.CheckMaintenanceCasualMatchResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/CheckMaintenanceFree",
    Protocol.Live.CheckMaintenanceFreeRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Live.CheckMaintenanceFreeResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/CheckMaintenanceRankMatch",
    Protocol.Live.CheckMaintenanceRankMatchRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Live.CheckMaintenanceRankMatchResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/GetBattleLiveSearchId",
    Protocol.Live.GetBattleLiveSearchIdRequest.Parser,
    (ctx, _) =>
    {
        var protocol = ctx.RequestServices.GetRequiredService<LiveProtocolBuilder>();
        return Task.FromResult(protocol.GetBattleLiveSearchId());
    });

app.MapGrpcUnary(
    "/app.live.LiveService/StartBattleLive",
    Protocol.Live.StartBattleLiveRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Live.StartBattleLiveResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/SaveSetting",
    Protocol.Live.SaveSettingRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<LiveProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        players.UpdateLiveSetting(player, request.SettingAll.ToByteArray());
        return Task.FromResult(protocol.SaveSetting());
    });

app.MapGrpcUnary(
    "/app.liveboost.LiveBoostService/LiveBoostReqCheck",
    Empty.Parser,
    static (_, _) => Task.FromResult(new Protocol.LiveBoost.LiveBoostReqCheckResponse()));

app.MapGrpcUnary(
    "/app.livemusic.LiveMusicService/GetRanking",
    Protocol.LiveMusic.GetRankingRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.LiveMusic.GetRankingResponse()));

app.MapGrpcUnary(
    "/app.livemusic.LiveMusicService/SaveFavorite",
    Protocol.LiveMusic.SaveFavoriteRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.LiveMusic.SaveFavoriteResponse()));

app.MapGrpcUnary(
    "/app.shop.ShopService/CheckMaintenanceShop",
    Protocol.Shop.CheckMaintenanceShopRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Shop.CheckMaintenanceShopResponse()));

app.MapGrpcUnary(
    "/app.shop.ShopService/Fetch",
    Protocol.Shop.FetchRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        var birth = players.GetShopBirth(player);
        return Task.FromResult(new Protocol.Shop.FetchResponse
        {
            Year = birth.Year,
            Month = birth.Month
        });
    });

app.MapGrpcUnary(
    "/app.shop.ShopService/SaveBirth",
    Protocol.Shop.SaveBirthRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveBirth(player, request.Year, request.Month);
        return Task.FromResult(new Protocol.Shop.SaveBirthResponse());
    });

app.MapGrpcUnary(
    "/app.shop.ShopService/History",
    Protocol.Shop.HistoryRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Shop.HistoryResponse()));

app.MapGrpcUnary(
    "/app.story.StoryService/CheckMaintenanceStory",
    Protocol.Story.CheckMaintenanceStoryRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Story.CheckMaintenanceStoryResponse()));

app.MapGrpcUnary(
    "/app.story.StoryService/ReadStoryEpisode",
    Protocol.Story.ReadStoryEpisodeRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.ReadStoryEpisode(player, request.EpisodeId, request.IsSkipped);
        return Task.FromResult(new Protocol.Story.ReadStoryEpisodeResponse());
    });

app.MapGrpcUnary(
    "/app.story.StoryService/ReadFriendshipEpisode",
    Protocol.Story.ReadFriendshipEpisodeRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.ReadFriendshipEpisode(player, request.EpisodeId, request.IsSkipped);
        return Task.FromResult(new Protocol.Story.ReadFriendshipEpisodeResponse());
    });

app.MapGrpcUnary(
    "/app.external_payments.ExternalPaymentsService/Nop",
    Empty.Parser,
    static (_, _) => Task.FromResult(new Empty()));

app.MapStaticAssetEndpoints();

app.MapFallback(async context =>
{
    if (GrpcFraming.IsGrpcRequest(context.Request))
        await GrpcFraming.WriteErrorAsync(context, "12", "rpc method is not supported");
    else
        context.Response.StatusCode = StatusCodes.Status404NotFound;
});

app.MapGet("/", () => Results.Json(new
{
    name = "MyNotes",
    status = "ok"
}));

var scheme = serverCertificate == null ? "http" : "https";
Console.WriteLine($"MyNotes gRPC HTTP/2 is running on {scheme} port {ServerConfig.GrpcPort}");
Console.WriteLine($"MyNotes static/admin HTTP/1.1 is running on {scheme} port {ServerConfig.HttpPort}");
app.Run();

public partial class Program { }
