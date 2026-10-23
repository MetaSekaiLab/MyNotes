using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyNotes.Config;
using MyNotes.Middleware;
using MyNotes.Services;
using Xunit;

namespace MyNotes.Tests;

public sealed class IdentityTests
{
    private readonly PlayerManager _players = new(NullLogger<PlayerManager>.Instance);

    public IdentityTests() => ConfigureAuthentication();

    [Fact]
    public void MissingCredentialsRequireAuthentication()
    {
        var request = new DefaultHttpContext().Request;

        Assert.False(_players.IsKnownCredential(request));
        Assert.Throws<UnauthorizedAccessException>(() => _players.GetFromRequest(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Bearer invalid")]
    [InlineData("Basic invalid")]
    [InlineData("unknown")]
    public void InvalidCredentialsRequireAuthentication(string authorization)
    {
        var request = Request(authorization);

        Assert.False(_players.IsKnownCredential(request));
        Assert.Throws<UnauthorizedAccessException>(() => _players.GetFromRequest(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisteredCredentialsResolveThePlayer(bool bearer)
    {
        var player = _players.Register("test");
        var request = Request((bearer ? "Bearer " : "") + player.AuthorizationKey);

        Assert.True(_players.IsKnownCredential(request));
        Assert.Same(player, _players.GetFromRequest(request));
    }

    [Fact]
    public void BearerSchemeIsCaseInsensitive()
    {
        var player = _players.Register("test");

        Assert.Same(player, _players.GetFromRequest(Request("bearer " + player.AuthorizationKey)));
    }

    [Fact]
    public void PlayerCredentialHeadersResolveMatchingPlayer()
    {
        var player = _players.Register("test");
        var request = new DefaultHttpContext().Request;
        request.Headers["x-player-id"] = player.PlayerId;
        request.Headers["x-player-credential"] = player.AuthorizationKey;

        Assert.Same(player, _players.GetFromRequest(request));
        request.Headers.Authorization = ServerConfig.ExpectedBasicAuth;
        Assert.Same(player, _players.GetFromRequest(request));
        request.Headers.Authorization = "Bearer " + player.AuthorizationKey;
        Assert.Same(player, _players.GetFromRequest(request));
        _players.Remove(player);
        Assert.False(_players.IsKnownCredential(request));
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("wrong-id")]
    [InlineData("wrong-credential")]
    [InlineData("empty-credential")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-credential")]
    [InlineData("conflicting-authorization")]
    [InlineData("duplicate-authorization")]
    public void InvalidPlayerCredentialHeadersRequireAuthentication(string scenario)
    {
        var player = _players.Register("test");
        var other = _players.Register("test");
        var request = new DefaultHttpContext().Request;
        request.Headers["x-player-id"] = player.PlayerId;
        request.Headers["x-player-credential"] = player.AuthorizationKey;
        switch (scenario)
        {
            case "missing-id": request.Headers.Remove("x-player-id"); break;
            case "wrong-id": request.Headers["x-player-id"] = other.PlayerId; break;
            case "wrong-credential": request.Headers["x-player-credential"] = "invalid"; break;
            case "empty-credential": request.Headers["x-player-credential"] = ""; break;
            case "duplicate-id": request.Headers.Append("x-player-id", player.PlayerId); break;
            case "duplicate-credential": request.Headers.Append("x-player-credential", player.AuthorizationKey); break;
            case "conflicting-authorization": request.Headers.Authorization = "Bearer " + other.AuthorizationKey; break;
            case "duplicate-authorization":
                request.Headers.Append("authorization", player.AuthorizationKey);
                request.Headers.Append("authorization", player.AuthorizationKey);
                break;
        }

        Assert.False(_players.IsKnownCredential(request));
        Assert.Throws<UnauthorizedAccessException>(() => _players.GetFromRequest(request));
    }

    [Fact]
    public void RegisteredCredentialSelectsItsOwnPlayer()
    {
        var first = _players.Register("test");
        var second = _players.Register("test");
        var request = Request("Bearer " + first.AuthorizationKey, second.PlayerId);

        Assert.Same(first, _players.GetFromRequest(request));
    }

    [Theory]
    [InlineData("x-player-id")]
    [InlineData("player-id")]
    [InlineData("playerid")]
    [InlineData("x-playerid")]
    public void PlayerSelectionRequiresConfiguredBasicAuthentication(string header)
    {
        var player = _players.Register("test");
        var request = new DefaultHttpContext().Request;
        request.Headers[header] = player.PlayerId;

        Assert.False(_players.IsKnownCredential(request));
        Assert.Throws<UnauthorizedAccessException>(() => _players.GetFromRequest(request));

        request.Headers.Authorization = ServerConfig.ExpectedBasicAuth;

        Assert.True(_players.IsKnownCredential(request));
        Assert.Same(player, _players.GetFromRequest(request));
    }

    [Theory]
    [InlineData("Bearer invalid")]
    [InlineData("Basic invalid")]
    [InlineData("")]
    public void InvalidAuthorizationCannotUsePlayerSelection(string authorization)
    {
        var player = _players.Register("test");
        var request = Request(authorization, player.PlayerId);

        Assert.False(_players.IsKnownCredential(request));
        Assert.Throws<UnauthorizedAccessException>(() => _players.GetFromRequest(request));
    }

    [Fact]
    public void BasicAuthenticationRequiresAnExplicitPlayer()
    {
        _players.Register("test");
        var request = Request(ServerConfig.ExpectedBasicAuth);

        Assert.False(_players.IsKnownCredential(request));
        Assert.Throws<UnauthorizedAccessException>(() => _players.GetFromRequest(request));
    }

    [Fact]
    public void MultipleAuthorizationValuesRequireAuthentication()
    {
        var player = _players.Register("test");
        var request = new DefaultHttpContext().Request;
        request.Headers.Append("authorization", player.AuthorizationKey);
        request.Headers.Append("authorization", ServerConfig.ExpectedBasicAuth);
        request.Headers["x-player-id"] = player.PlayerId;

        Assert.False(_players.IsKnownCredential(request));
        Assert.Throws<UnauthorizedAccessException>(() => _players.GetFromRequest(request));
    }

    [Fact]
    public void RemovedCredentialsRequireAuthentication()
    {
        var player = _players.Register("test");
        var request = Request("Bearer " + player.AuthorizationKey);
        _players.Remove(player);

        Assert.False(_players.IsKnownCredential(request));
        Assert.Throws<UnauthorizedAccessException>(() => _players.GetFromRequest(request));
    }

    [Fact]
    public void MissingCredentialsDoNotRegisterAPlayer()
    {
        var expectedProfileId = new PlayerManager(NullLogger<PlayerManager>.Instance).Register("test").ProfileId;
        Assert.Throws<UnauthorizedAccessException>(() => _players.GetFromRequest(new DefaultHttpContext().Request));
        var player = _players.Register("test");

        Assert.Equal(expectedProfileId, player.ProfileId);
    }

    [Fact]
    public void BasicAuthenticationMustBeConfiguredForPlayerSelection()
    {
        var authorization = ServerConfig.ExpectedBasicAuth;
        var player = _players.Register("test");
        ConfigureAuthentication(user: "", password: "");
        var request = Request(authorization, player.PlayerId);

        Assert.False(_players.IsKnownCredential(request));
        Assert.Throws<UnauthorizedAccessException>(() => _players.GetFromRequest(request));
    }

    [Theory]
    [InlineData("Basic credential", "Basic")]
    [InlineData("Bearer credential", "Bearer")]
    [InlineData("credential", "Other")]
    [InlineData("custom-scheme credential", "Other")]
    public async Task AuthorizationLoggingUsesFixedSchemeNames(string authorization, string scheme)
    {
        var logger = new CapturingLogger<RequestLoggingScopeMiddleware>();
        var invoked = false;
        var middleware = new RequestLoggingScopeMiddleware(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, logger);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = authorization;

        await middleware.Invoke(context);

        Assert.True(invoked);
        Assert.Equal("authorization header present: " + scheme, Assert.Single(logger.Messages));
    }

    private static void ConfigureAuthentication(string user = "test-user", string password = "test-password") =>
        ServerConfig.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Game:BasicAuthUser"] = user,
            ["Game:BasicAuthPass"] = password,
            ["Game:RequireBasicAuth"] = "false",
            ["Game:InitialDataGroup"] = "test"
        }).Build());

    private static HttpRequest Request(string authorization, string? playerId = null)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = authorization;
        if (playerId != null)
            request.Headers["x-player-id"] = playerId;
        return request;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
