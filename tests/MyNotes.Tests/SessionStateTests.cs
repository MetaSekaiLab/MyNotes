using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using App.Protobuf.Chat;
using App.Protobuf.Live;
using App.Protobuf.Player;
using App.Protobuf.PlayerExt;
using App.Protobuf.SeasonPass;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyNotes.Config;
using MyNotes.Middleware;
using MyNotes.Services;
using Xunit;

namespace MyNotes.Tests;

public sealed class SessionStateTests
{
    [Fact]
    public void SyncDataReportsAccountIdWithDefaultGemAndBoostSections()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        ServerConfig.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Game:RequireBasicAuth"] = "false" }).Build());
        var master = new MasterDataService(fixture.Environment, NullLogger<MasterDataService>.Instance, fixture.CreateRelease());
        var players = new PlayerManager(NullLogger<PlayerManager>.Instance);
        var player = players.Register("Default");
        var protocol = new PlayerProtocolBuilder(master);

        foreach (var candidate in new[] { protocol.SyncData(player), SyncDataResponse.Parser.ParseFrom(protocol.SyncData(player).ToByteArray()) })
        {
            Assert.Equal(player.ProfileId, candidate.Accountid);
            Assert.NotNull(candidate.Gem);
            Assert.NotNull(candidate.LiveBoost);
            Assert.Equal(0, candidate.Gem.Free);
            Assert.Equal(0, candidate.LiveBoost.Amount);
        }
    }

    [Fact]
    public async Task CompanionEndpointsReplyWithDefaultState()
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        ServerConfig.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Game:RequireBasicAuth"] = "false" }).Build());
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0,
            listener => listener.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
        builder.Services.AddSingleton(fixture.Environment);
        builder.Services.AddSingleton(fixture.CreateRelease());
        builder.Services.AddSingleton<MasterDataService>();
        builder.Services.AddSingleton<PlayerManager>();
        builder.Services.AddSingleton<PlayerProtocolBuilder>();
        await using var app = builder.Build();
        app.MapGrpcUnary("/app.player.PlayerService/Register", RegisterRequest.Parser,
            (context, request) => Task.FromResult(context.RequestServices.GetRequiredService<PlayerProtocolBuilder>()
                .Register(context.RequestServices.GetRequiredService<PlayerManager>().Register(request.InitialDataGroup))));
        app.MapGrpcUnary("/app.playerext.PlayerExtService/SyncData", SyncDataRequest.Parser,
            (context, _) => Task.FromResult(context.RequestServices.GetRequiredService<PlayerProtocolBuilder>()
                .SyncData(context.RequestServices.GetRequiredService<PlayerManager>().GetFromRequest(context.Request))));
        app.MapGrpcUnary("/app.seasonpass.SeasonPassService/Fetch", FetchRequest.Parser,
            static (_, _) => Task.FromResult(new FetchResponse()));
        app.MapGrpcUnary("/app.chat.ClientChatService/SyncMsg", ClientSyncMsgRequest.Parser,
            static (_, _) => Task.FromResult(new ClientSyncMsgResponse()));
        app.MapGrpcUnary("/app.chat.ClientChatService/GetNotifications", GetNotificationsRequest.Parser,
            static (_, _) => Task.FromResult(new GetNotificationsResponse()));
        app.MapGrpcUnary("/app.live.LiveService/FetchBattleLiveInvitation", FetchBattleLiveInvitationRequest.Parser,
            static (_, _) => Task.FromResult(new FetchBattleLiveInvitationResponse()));
        app.MapGrpcUnary("/app.live.LiveService/CheckMaintenanceFree", CheckMaintenanceFreeRequest.Parser,
            static (_, _) => Task.FromResult(new CheckMaintenanceFreeResponse()));
        await app.StartAsync();
        using var handler = new HttpClientHandler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(app.Urls.ToArray()[0]),
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var registration = RegisterResponse.Parser.ParseFrom(
            await Call(client, "/app.player.PlayerService/Register", new RegisterRequest { InitialDataGroup = "Default" }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registration.Credential!.Credential);

        var sync = SyncDataResponse.Parser.ParseFrom(await Call(client, "/app.playerext.PlayerExtService/SyncData", new SyncDataRequest()));
        Assert.Equal(registration.ProfileId, sync.Accountid);
        Assert.NotNull(sync.Gem);
        Assert.NotNull(sync.LiveBoost);
        Assert.Empty(new FetchResponse().SeasonPasses);
        Assert.Empty(new GetNotificationsResponse().Notifications);
        Assert.Empty(new FetchBattleLiveInvitationResponse().Invitations);
        Assert.Empty(await Call(client, "/app.seasonpass.SeasonPassService/Fetch", new FetchRequest()));
        Assert.Empty(await Call(client, "/app.chat.ClientChatService/SyncMsg", new ClientSyncMsgRequest()));
        Assert.Empty(await Call(client, "/app.chat.ClientChatService/GetNotifications", new GetNotificationsRequest()));
        Assert.Empty(await Call(client, "/app.live.LiveService/FetchBattleLiveInvitation", new FetchBattleLiveInvitationRequest()));
        Assert.Empty(await Call(client, "/app.live.LiveService/CheckMaintenanceFree", new CheckMaintenanceFreeRequest()));
        await app.StopAsync();
    }

    private static async Task<byte[]> Call(HttpClient client, string path, IMessage message)
    {
        var payload = message.ToByteArray();
        var body = new byte[payload.Length + 5];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(body, 5);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        using var response = await client.PostAsync(path, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(System.Net.HttpVersion.Version20, response.Version);
        var data = await response.Content.ReadAsByteArrayAsync();
        var status = response.TrailingHeaders.TryGetValues("grpc-status", out var trailer) ? trailer.Single() :
            response.Headers.GetValues("grpc-status").Single();
        var grpcMessage = response.TrailingHeaders.TryGetValues("grpc-message", out var messages) ? messages.Single() :
            response.Headers.TryGetValues("grpc-message", out var headers) ? headers.Single() : "";
        Assert.True(status == "0", $"{path}: grpc-status={status}, message={grpcMessage}");
        Assert.True(data.Length >= 5);
        Assert.Equal(data.Length - 5, (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1, 4)));
        return data[5..];
    }
}
