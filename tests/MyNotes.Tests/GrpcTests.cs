using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MyNotes.Config;
using MyNotes.Middleware;
using MyNotes.Services;
using Xunit;

namespace MyNotes.Tests;

public sealed class GrpcTests
{
    public GrpcTests() => ConfigureAuthentication(false);

    [Theory]
    [InlineData("application/grpc")]
    [InlineData("application/grpc+proto")]
    [InlineData("APPLICATION/GRPC")]
    [InlineData("application/grpc+proto; charset=utf-8")]
    [InlineData("application/grpc; charset=\"utf-8\"")]
    public void RecognizesGrpcContentTypes(string contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;

        Assert.True(GrpcFraming.IsGrpcRequest(context.Request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("application/grpc-web")]
    [InlineData("application/grpc+json")]
    [InlineData("application/grpc-extra")]
    [InlineData("application/grpc/extra")]
    [InlineData("application/json")]
    [InlineData("application/grpc; invalid")]
    [InlineData("application/grpc; invalid=")]
    [InlineData("application/grpc; charset=utf-8; invalid")]
    [InlineData("application/grpc; charset=\"utf-8")]
    public void RejectsOtherContentTypes(string? contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;

        Assert.False(GrpcFraming.IsGrpcRequest(context.Request));
    }

    public static IEnumerable<object[]> MalformedFrames()
    {
        yield return [Array.Empty<byte>()];
        yield return [new byte[] { 0 }];
        yield return [new byte[] { 0, 0, 0, 0 }];
        yield return [new byte[] { 2, 0, 0, 0, 0 }];
        yield return [new byte[] { 255, 0, 0, 0, 0 }];
        yield return [new byte[] { 0, 0, 0, 0, 1 }];
        yield return [new byte[] { 0, 0, 0, 0, 2, 1 }];
        yield return [new byte[] { 0, 0, 0, 0, 0, 0 }];
        yield return [new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }];
    }

    [Theory]
    [MemberData(nameof(MalformedFrames))]
    public async Task RejectsMalformedUnaryFrames(byte[] body)
    {
        var request = RequestWithBody(body);

        var exception = await Assert.ThrowsAsync<GrpcFrameException>(
            () => GrpcFraming.ReadRequestAsync(request, CancellationToken.None));

        Assert.Equal("3", exception.Status);
    }

    [Fact]
    public async Task ReadsAnEmptyProtobufMessageFromACompleteFrame()
    {
        var frame = await GrpcFraming.ReadRequestAsync(RequestWithBody(new byte[5]), CancellationToken.None);

        Assert.False(frame.Compressed);
        Assert.True(frame.Payload.IsEmpty);
    }

    [Fact]
    public async Task ReadsAFrameAcrossPartialStreamReads()
    {
        var request = new DefaultHttpContext().Request;
        request.Body = new PartialReadStream(Frame([1, 2, 3]));

        var frame = await GrpcFraming.ReadRequestAsync(request, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Payload.ToArray());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public async Task RequiresContentLengthToMatchTheFrame(long contentLength)
    {
        var request = RequestWithBody(new byte[5]);
        request.ContentLength = contentLength;

        var exception = await Assert.ThrowsAsync<GrpcFrameException>(
            () => GrpcFraming.ReadRequestAsync(request, CancellationToken.None));

        Assert.Equal("3", exception.Status);
    }

    [Fact]
    public async Task RejectsOversizedDeclaredMessagesBeforeReadingPayload()
    {
        var prefix = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(1), (uint)GrpcFraming.MaxMessageLength + 1);
        var request = RequestWithBody(prefix);

        var exception = await Assert.ThrowsAsync<GrpcFrameException>(
            () => GrpcFraming.ReadRequestAsync(request, CancellationToken.None));

        Assert.Equal("8", exception.Status);
        Assert.Equal(5, request.Body.Position);
    }

    [Fact]
    public async Task RejectsOversizedContentLengthBeforeReadingTheBody()
    {
        var request = RequestWithBody(new byte[5]);
        request.ContentLength = GrpcFraming.MaxMessageLength + 6L;

        var exception = await Assert.ThrowsAsync<GrpcFrameException>(
            () => GrpcFraming.ReadRequestAsync(request, CancellationToken.None));

        Assert.Equal("8", exception.Status);
        Assert.Equal(0, request.Body.Position);
    }

    [Fact]
    public async Task AcceptsTheMessageSizeLimit()
    {
        var body = new byte[GrpcFraming.MaxMessageLength + 5];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1), GrpcFraming.MaxMessageLength);
        var request = RequestWithBody(body);
        request.ContentLength = body.Length;

        var frame = await GrpcFraming.ReadRequestAsync(request, CancellationToken.None);

        Assert.Equal(GrpcFraming.MaxMessageLength, frame.Payload.Length);
    }

    [Theory]
    [MemberData(nameof(MalformedFrames))]
    public async Task EndpointMapsMalformedFramesToInvalidArgument(byte[] body)
    {
        using var host = await StartHostAsync();
        using var response = await SendAsync(host, "/echo", body);

        AssertGrpcStatus(response, "3");
    }

    [Fact]
    public async Task EndpointMapsMalformedProtobufToInvalidArgument()
    {
        using var host = await StartHostAsync();
        using var response = await SendAsync(host, "/echo", Frame([0]));

        AssertGrpcStatus(response, "3");
    }

    [Fact]
    public async Task EndpointMapsOversizedMessagesToResourceExhausted()
    {
        var prefix = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(1), (uint)GrpcFraming.MaxMessageLength + 1);
        using var host = await StartHostAsync();
        using var response = await SendAsync(host, "/echo", prefix);

        AssertGrpcStatus(response, "8");
    }

    [Fact]
    public async Task EndpointMapsCompressedMessagesToUnimplemented()
    {
        using var host = await StartHostAsync();
        using var response = await SendAsync(host, "/echo", Frame([], compressed: true));

        AssertGrpcStatus(response, "12");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/grpc-web")]
    public async Task EndpointRequiresAGrpcContentType(string contentType)
    {
        using var host = await StartHostAsync();
        using var response = await SendAsync(host, "/echo", Frame([]), contentType: contentType);

        AssertGrpcStatus(response, "3");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EndpointRejectsHttp1BeforeExecutingHandler(int minorVersion)
    {
        var handlerCalls = 0;
        using var host = await StartHostAsync(echoHandlerExecuted: () => Interlocked.Increment(ref handlerCalls));
        using var response = await SendAsync(host, "/echo", Frame([]), version: new Version(1, minorVersion));

        AssertGrpcStatus(response, "12");
        Assert.Equal(0, handlerCalls);
    }

    [Theory]
    [InlineData(400, "3", 0)]
    [InlineData(413, "8", 0)]
    [InlineData(400, "3", 5)]
    [InlineData(413, "8", 5)]
    [InlineData(400, "3", 7)]
    [InlineData(413, "8", 7)]
    public async Task EndpointMapsRequestBodyFailuresBeforeExecutingHandler(int statusCode, string grpcStatus, int failurePosition)
    {
        var handlerCalls = 0;
        var body = Frame([8, 1]);
        using var host = await StartHostAsync(
            echoHandlerExecuted: () => Interlocked.Increment(ref handlerCalls),
            requestBody: () => new BadRequestReadStream(body, statusCode, failurePosition));
        using var response = await SendAsync(host, "/echo", body);

        AssertGrpcStatus(response, grpcStatus);
        Assert.Equal(0, handlerCalls);
    }

    [Fact]
    public async Task EndpointWritesOneSuccessFrame()
    {
        using var host = await StartHostAsync();
        using var response = await SendAsync(host, "/echo", Frame([]), contentType: "application/grpc+proto");

        AssertGrpcStatus(response, "0");
        Assert.Equal(new byte[5], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task EndpointPreservesTypedHandlerStatus()
    {
        using var host = await StartHostAsync();
        using var response = await SendAsync(host, "/status", Frame([]));

        AssertGrpcStatus(response, "9");
    }

    [Fact]
    public async Task PublicEndpointAcceptsRequestsWithoutAuthorization()
    {
        using var host = await StartHostAsync();
        using var response = await SendAsync(host, "/echo", Frame([]));

        AssertGrpcStatus(response, "0");
    }

    [Fact]
    public async Task BasicAuthenticationRequirementRejectsAMissingHeader()
    {
        ConfigureAuthentication(true);
        using var host = await StartHostAsync();
        using var response = await SendAsync(host, "/echo", Frame([]));

        AssertGrpcStatus(response, "16");
    }

    [Fact]
    public async Task BasicAuthenticationRequirementAcceptsTheConfiguredCredential()
    {
        ConfigureAuthentication(true);
        using var host = await StartHostAsync();
        using var response = await SendAsync(host, "/echo", Frame([]), ServerConfig.ExpectedBasicAuth);

        AssertGrpcStatus(response, "0");
    }

    [Fact]
    public async Task BasicAuthenticationRequirementRejectsAPlayerCredential()
    {
        ConfigureAuthentication(true);
        var players = NewPlayers();
        var player = players.Register("test");
        using var host = await StartHostAsync(players);
        using var response = await SendAsync(host, "/echo", Frame([]), "Bearer " + player.AuthorizationKey);

        AssertGrpcStatus(response, "16");
    }

    [Fact]
    public async Task PlayerEndpointRequiresAnAuthenticatedIdentity()
    {
        using var host = await StartHostAsync();
        using var response = await SendAsync(host, "/identity", Frame([]));

        AssertGrpcStatus(response, "16");
    }

    [Fact]
    public async Task InvalidAuthorizationCannotSelectAPlayerByHeader()
    {
        var players = NewPlayers();
        var player = players.Register("test");
        using var host = await StartHostAsync(players);
        using var response = await SendAsync(host, "/identity", Frame([]), "Bearer invalid", playerId: player.PlayerId);

        AssertGrpcStatus(response, "16");
    }

    [Fact]
    public async Task PlayerEndpointAcceptsARegisteredBearerCredential()
    {
        var players = NewPlayers();
        var player = players.Register("test");
        using var host = await StartHostAsync(players);
        using var response = await SendAsync(host, "/identity", Frame([]), "Bearer " + player.AuthorizationKey);

        AssertGrpcStatus(response, "0");
    }

    [Fact]
    public async Task ConfiguredBasicAuthenticationCanSelectAnExplicitPlayer()
    {
        var players = NewPlayers();
        var player = players.Register("test");
        using var host = await StartHostAsync(players);
        using var response = await SendAsync(host, "/identity", Frame([]), ServerConfig.ExpectedBasicAuth, playerId: player.PlayerId);

        AssertGrpcStatus(response, "0");
    }

    private static void ConfigureAuthentication(bool required) => ServerConfig.Load(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Game:BasicAuthUser"] = "test-user",
            ["Game:BasicAuthPass"] = "test-password",
            ["Game:RequireBasicAuth"] = required.ToString(),
            ["Game:InitialDataGroup"] = "test"
        }).Build());

    private static PlayerManager NewPlayers() => new(NullLogger<PlayerManager>.Instance);

    private static async Task<IHost> StartHostAsync(
        PlayerManager? players = null,
        Action? echoHandlerExecuted = null,
        Func<Stream>? requestBody = null) =>
        await new HostBuilder().ConfigureWebHost(builder => builder
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton(players ?? NewPlayers());
            })
            .Configure(app =>
            {
                if (requestBody != null)
                    app.Use(async (context, next) =>
                    {
                        using var body = requestBody();
                        context.Request.Body = body;
                        await next(context);
                    });
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGrpcUnary("/echo", Empty.Parser, (_, request) =>
                    {
                        echoHandlerExecuted?.Invoke();
                        return Task.FromResult(request);
                    });
                    endpoints.MapGrpcUnary("/status", static (_, _) =>
                        throw new GrpcStatusException("9", "precondition failed"));
                    endpoints.MapGrpcUnary("/identity", Empty.Parser, (context, request) =>
                    {
                        context.RequestServices.GetRequiredService<PlayerManager>().GetFromRequest(context.Request);
                        return Task.FromResult(request);
                    });
                });
            })).StartAsync();

    private static async Task<HttpResponseMessage> SendAsync(
        IHost host,
        string path,
        byte[] body,
        string? authorization = null,
        string contentType = GrpcFraming.ContentType,
        string? playerId = null,
        Version? version = null)
    {
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(body),
            Version = version ?? HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (authorization != null)
            request.Headers.TryAddWithoutValidation("authorization", authorization);
        if (playerId != null)
            request.Headers.TryAddWithoutValidation("x-player-id", playerId);
        return await client.SendAsync(request);
    }

    private static void AssertGrpcStatus(HttpResponseMessage response, string expected)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GrpcFraming.ContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.TryGetValues("grpc-status", out var values) ||
                    response.TrailingHeaders.TryGetValues("grpc-status", out values));
        Assert.Equal(expected, Assert.Single(values!));
    }

    private static HttpRequest RequestWithBody(byte[] body)
    {
        var request = new DefaultHttpContext().Request;
        request.Body = new MemoryStream(body, writable: false);
        return request;
    }

    private static byte[] Frame(byte[] payload, bool compressed = false)
    {
        var body = new byte[payload.Length + 5];
        body[0] = compressed ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(body, 5);
        return body;
    }

    private sealed class PartialReadStream(byte[] body) : MemoryStream(body, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }

    private sealed class BadRequestReadStream(byte[] body, int statusCode, int failurePosition) : MemoryStream(body, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= failurePosition)
                return ValueTask.FromException<int>(new BadHttpRequestException("request body could not be read", statusCode));

            return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, failurePosition - Position)], cancellationToken);
        }
    }
}
