using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using App.Protobuf.Entity;
using App.Protobuf.Live;
using App.Protobuf.Masterdata;
using App.Protobuf.Player;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyNotes.Config;
using MyNotes.Controllers;
using MyNotes.Middleware;
using MyNotes.Services;
using Xunit;

namespace MyNotes.Tests;

public sealed class IntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task VersionDownloadRegistrationAndFreeLiveUseOneRelease(bool tls, bool playerHeaders)
    {
        using var fixture = new ResourceReleaseFixture(includeMasterJson: true);
        fixture.WriteTable("MasterLiveMusic", new { _id = 11, _easyID = 73, _normalID = 0, _hardID = 0, _expertID = 0 });
        fixture.WriteTable("MasterLiveMusicScore", new { _id = 73 });
        fixture.WriteTable("MasterMemberCard", new { _id = 41 });
        fixture.WriteTable("MasterSupportCard", new { _id = 42 });
        fixture.WriteTable("MasterPlayerData",
            new { _id = 1, _group = "Default", _playerDataType = 1, _parameter1 = "41" },
            new { _id = 2, _group = "Default", _playerDataType = 2, _parameter1 = "42" },
            new { _id = 3, _group = "Default", _playerDataType = 5, _parameter0 = "1", _parameter1 = "Deck", _parameter2 = "1", _parameter3 = "1" });
        var release = fixture.CreateRelease();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Static:UpstreamEnabled"] = "false", ["Game:RequireBasicAuth"] = "false" }).Build();
        ServerConfig.Load(configuration);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listener =>
            {
                listener.Protocols = HttpProtocols.Http2;
                if (tls) listener.UseHttps(certificate);
            });
            options.Listen(IPAddress.Loopback, 0, listener =>
            {
                listener.Protocols = HttpProtocols.Http1;
                if (tls) listener.UseHttps(certificate);
            });
        });
        builder.Services.AddSingleton(fixture.Environment);
        builder.Services.AddSingleton(release);
        builder.Services.AddSingleton<MasterDataService>();
        builder.Services.AddSingleton<PlayerManager>();
        builder.Services.AddSingleton<PlayerProtocolBuilder>();
        builder.Services.AddSingleton<FreeLiveService>();
        builder.Services.AddSingleton<StaticAssetService>();
        builder.Services.AddHttpClient("official-assets");
        await using var app = builder.Build();
        app.MapGrpcUnary("/app.masterdata.MasterdataService/Version", VersionRequest.Parser,
            (_, _) => Task.FromResult(new VersionResponse { Version = release.MasterVersion, ResourceVersion = release.ResourceVersion }));
        app.MapGrpcUnary("/app.player.PlayerService/Register", RegisterRequest.Parser,
            (context, request) => Task.FromResult(context.RequestServices.GetRequiredService<PlayerProtocolBuilder>()
                .Register(context.RequestServices.GetRequiredService<PlayerManager>().Register(request.InitialDataGroup))));
        app.MapGrpcUnary("/app.live.LiveService/StartFree", StartFreeRequest.Parser,
            (context, request) => Task.FromResult(context.RequestServices.GetRequiredService<FreeLiveService>()
                .Start(context.RequestServices.GetRequiredService<PlayerManager>().GetFromRequest(context.Request), request)));
        app.MapGrpcUnary("/app.live.LiveService/FinishFree", FinishFreeRequest.Parser,
            (context, request) => Task.FromResult(context.RequestServices.GetRequiredService<FreeLiveService>()
                .Finish(context.RequestServices.GetRequiredService<PlayerManager>().GetFromRequest(context.Request), request)));
        app.MapGrpcUnary("/app.player.PlayerService/GetPlayerData", GetPlayerDataRequest.Parser,
            (context, _) =>
            {
                var players = context.RequestServices.GetRequiredService<PlayerManager>();
                return Task.FromResult(context.RequestServices.GetRequiredService<PlayerProtocolBuilder>()
                    .GetPlayerData(players.GetFromRequest(context.Request), players));
            });
        app.MapStaticAssetEndpoints();
        await app.StartAsync();
        var urls = app.Urls.ToArray();
        using var grpcHandler = new HttpClientHandler();
        using var httpHandler = new HttpClientHandler();
        if (tls)
        {
            grpcHandler.ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.Thumbprint == certificate.Thumbprint;
            httpHandler.ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.Thumbprint == certificate.Thumbprint;
        }
        using var grpc = new HttpClient(grpcHandler) { BaseAddress = new Uri(urls[0]), DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var http = new HttpClient(httpHandler) { BaseAddress = new Uri(urls[1]) };
        using var http1Body = new ByteArrayContent(new byte[5]);
        http1Body.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        using var http1Rpc = await http.PostAsync("/app.masterdata.MasterdataService/Version", http1Body);
        Assert.Equal("12", http1Rpc.Headers.GetValues("grpc-status").Single());
        var version = VersionResponse.Parser.ParseFrom(await Call(grpc, "/app.masterdata.MasterdataService/Version", new VersionRequest()));
        Assert.Equal(release.MasterVersion, version.Version);
        Assert.Equal(release.ResourceVersion, version.ResourceVersion);
        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/asset/Android/" + fixture.CatalogName + ".bin"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(new FileInfo(fixture.FilePath(fixture.CatalogPath)).Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var range = new HttpRequestMessage(HttpMethod.Get, "/asset/Android/sample.bundle");
        range.Headers.Range = new RangeHeaderValue(1, 3);
        using var partial = await http.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(File.ReadAllBytes(fixture.FilePath("Android/sample.bundle"))[1..4], await partial.Content.ReadAsByteArrayAsync());
        Assert.Equal(File.ReadAllBytes(fixture.FilePath(fixture.MasterManifestPath)),
            await http.GetByteArrayAsync($"/master/{version.Version}/MasterManifest.json"));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/master/unknown/sample.bytes")).StatusCode);
        var registration = RegisterResponse.Parser.ParseFrom(await Call(grpc, "/app.player.PlayerService/Register",
            new RegisterRequest { InitialDataGroup = "Default" }));
        if (playerHeaders)
        {
            grpc.DefaultRequestHeaders.Add("x-player-id", registration.Credential!.Id);
            grpc.DefaultRequestHeaders.Add("x-player-credential", registration.Credential.Credential);
        }
        else
            grpc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registration.Credential!.Credential);
        await Call(grpc, "/app.live.LiveService/StartFree", new StartFreeRequest { MusicId = 11, Difficulty = 1, DeckId = 1 });
        var finish = FinishFreeResponse.Parser.ParseFrom(await Call(grpc, "/app.live.LiveService/FinishFree",
            new FinishFreeRequest { Score = 55, Combo = 7, ClearStatus = 4 }));
        Assert.NotNull(finish.Notification);
        var player = GetPlayerDataResponse.Parser.ParseFrom(await Call(grpc, "/app.player.PlayerService/GetPlayerData", new GetPlayerDataRequest()));
        var saved = Assert.Single(player.PlayerData!.LiveScore);
        Assert.Equal(55, saved.HighScore);
        Assert.Equal(7, saved.MaxCombo);
        Assert.Equal(11, saved.MusicId);
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
        Assert.Equal(HttpVersion.Version20, response.Version);
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
