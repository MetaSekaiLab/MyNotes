using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyNotes.Config;
using MyNotes.Services;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace MyNotes.Tests;

public sealed class StaticAssetTests
{
    [Fact]
    public async Task LocalFilesTakePriorityOverCachedAndUpstreamFiles()
    {
        using var assets = new AssetHost(upstreamEnabled: false);
        assets.WriteLocal("sample.bundle", "local");
        assets.WriteCache("sample.bundle", "cached");

        using var response = await assets.Client.GetAsync("/asset/Android/sample.bundle");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("local", await response.Content.ReadAsStringAsync());
        Assert.Empty(assets.UpstreamRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalFilesSupportHeadWithOrWithoutCaching(bool cacheEnabled)
    {
        using var assets = new AssetHost(cacheEnabled: cacheEnabled, upstreamEnabled: false);
        assets.WriteLocal("sample.bundle", "abcdef");
        using var request = new HttpRequestMessage(HttpMethod.Head, "/asset/Android/sample.bundle");

        using var response = await assets.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(6L, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Empty(assets.UpstreamRequests);
    }

    [Fact]
    public async Task LocalFilesSupportRangesWhenUpstreamIsDisabled()
    {
        using var assets = new AssetHost(cacheEnabled: false, upstreamEnabled: false);
        assets.WriteLocal("sample.bundle", "abcdef");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/asset/Android/sample.bundle");
        request.Headers.Range = new RangeHeaderValue(1, 3);

        using var response = await assets.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bcd", await response.Content.ReadAsStringAsync());
        Assert.Equal("bytes 1-3/6", response.Content.Headers.ContentRange?.ToString());
        Assert.Empty(assets.UpstreamRequests);
    }

    [Fact]
    public async Task LocalRangeWithOlderIfRangeReturnsTheFullFile()
    {
        using var assets = new AssetHost(upstreamEnabled: false);
        assets.WriteLocal("sample.bundle", "abcdef");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/asset/Android/sample.bundle");
        request.Headers.Range = new RangeHeaderValue(1, 3);
        request.Headers.IfRange = new RangeConditionHeaderValue(DateTimeOffset.UnixEpoch);

        using var response = await assets.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("abcdef", await response.Content.ReadAsStringAsync());
        Assert.NotNull(response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task CachedFilesAreAvailableWhenUpstreamIsDisabled()
    {
        using var assets = new AssetHost(upstreamEnabled: false);
        assets.WriteCache("sample.bundle", "cached");

        using var response = await assets.Client.GetAsync("/asset/Android/sample.bundle");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cached", await response.Content.ReadAsStringAsync());
        Assert.Empty(assets.UpstreamRequests);
    }

    [Fact]
    public async Task MissingFilesReturnNotFoundWhenUpstreamIsDisabled()
    {
        using var assets = new AssetHost(upstreamEnabled: false);

        using var response = await assets.Client.GetAsync("/asset/Android/missing.bundle");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(assets.UpstreamRequests);
    }

    [Theory]
    [InlineData("../outside.bundle")]
    [InlineData("nested/../../outside.bundle")]
    [InlineData("nested\\..\\outside.bundle")]
    [InlineData("/outside.bundle")]
    [InlineData("C:\\outside.bundle")]
    [InlineData("sample.bundle:alternate")]
    [InlineData("nested/./sample.bundle")]
    [InlineData("NUL.bundle")]
    public async Task InvalidRelativePathsReturnBadRequest(string path)
    {
        using var assets = new AssetHost();

        var context = await assets.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/asset/Android/" + path;
        });

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Empty(assets.UpstreamRequests);
    }

    [SymbolicLinkFact]
    public async Task SymbolicLinksCannotExposeFilesOutsideTheLocalDirectory()
    {
        using var assets = new AssetHost(upstreamEnabled: false);
        var outside = Path.Combine(assets.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sample.bundle"), "outside");
        Directory.CreateDirectory(Path.Combine(assets.LocalRoot, "Android"));
        Directory.CreateSymbolicLink(Path.Combine(assets.LocalRoot, "Android", "linked"), outside);

        using var response = await assets.Client.GetAsync("/asset/Android/linked/sample.bundle");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(assets.UpstreamRequests);
    }

    [SymbolicLinkFact]
    public async Task FileSymbolicLinksCannotExposeFilesOutsideTheLocalDirectory()
    {
        using var assets = new AssetHost(upstreamEnabled: false);
        var outside = Path.Combine(assets.Root, "outside.bundle");
        File.WriteAllText(outside, "outside");
        Directory.CreateDirectory(Path.Combine(assets.LocalRoot, "Android"));
        File.CreateSymbolicLink(Path.Combine(assets.LocalRoot, "Android", "sample.bundle"), outside);

        using var response = await assets.Client.GetAsync("/asset/Android/sample.bundle");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(assets.UpstreamRequests);
    }

    [Fact]
    public async Task ColdRangesForwardRangeAndIfRangeWithoutPopulatingTheCache()
    {
        using var assets = new AssetHost((request, _) =>
        {
            var response = Response("bcd", HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 3, 6);
            return Task.FromResult(response);
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/asset/Android/sample.bundle");
        request.Headers.Range = new RangeHeaderValue(1, 3);
        request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"sample\""));

        using var response = await assets.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bcd", await response.Content.ReadAsStringAsync());
        Assert.Equal("bytes 1-3/6", response.Content.Headers.ContentRange?.ToString());
        var forwarded = Assert.Single(assets.UpstreamRequests);
        Assert.Equal(HttpMethod.Get, forwarded.Method);
        Assert.Equal("bytes=1-3", forwarded.Range);
        Assert.Equal("\"sample\"", forwarded.IfRange);
        Assert.False(File.Exists(assets.CachePath("sample.bundle")));
        Assert.Empty(assets.CacheTemporaryFiles());
    }

    [Fact]
    public async Task AFullUpstreamRangeResponseIsNotCached()
    {
        using var assets = new AssetHost();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/asset/Android/sample.bundle");
        request.Headers.Range = new RangeHeaderValue(1, 3);

        using var response = await assets.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream", await response.Content.ReadAsStringAsync());
        Assert.Single(assets.UpstreamRequests);
        Assert.False(File.Exists(assets.CachePath("sample.bundle")));
    }

    [Theory]
    [InlineData("catalog.bin")]
    [InlineData("catalog.hash")]
    [InlineData("catalog_alpha.bin")]
    [InlineData("nested/CATALOG_alpha.HASH")]
    [InlineData("catalog")]
    [InlineData("catalog_alpha")]
    public async Task CatalogMarkersBypassExistingCacheFiles(string path)
    {
        using var assets = new AssetHost();
        assets.WriteCache(path, "cached");

        using var response = await assets.Client.GetAsync("/asset/Android/" + path);

        Assert.Equal("upstream", await response.Content.ReadAsStringAsync());
        Assert.Equal("cached", File.ReadAllText(assets.CachePath(path)));
        Assert.Single(assets.UpstreamRequests);
        Assert.Empty(assets.CacheTemporaryFiles());
    }

    [Fact]
    public async Task LocalCatalogMarkersAreAvailableWhenUpstreamIsDisabled()
    {
        using var assets = new AssetHost(upstreamEnabled: false);
        assets.WriteLocal("catalog_alpha.bin", "local");
        assets.WriteCache("catalog_alpha.bin", "cached");

        using var response = await assets.Client.GetAsync("/asset/Android/catalog_alpha.bin");

        Assert.Equal("local", await response.Content.ReadAsStringAsync());
        Assert.Empty(assets.UpstreamRequests);
    }

    [Fact]
    public async Task CatalogMarkersWithoutLocalFilesAreNotFoundWhenUpstreamIsDisabled()
    {
        using var assets = new AssetHost(upstreamEnabled: false);
        assets.WriteCache("catalog_alpha.hash", "cached");

        using var response = await assets.Client.GetAsync("/asset/Android/catalog_alpha.hash");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(assets.UpstreamRequests);
    }

    [Fact]
    public async Task CompletedDownloadsAreCachedAndServedWithoutAnotherUpstreamRequest()
    {
        using var assets = new AssetHost();

        using var first = await assets.Client.GetAsync("/asset/Android/nested/sample.bundle");
        Assert.Equal("upstream", await first.Content.ReadAsStringAsync());
        using var second = await assets.Client.GetAsync("/asset/Android/nested/sample.bundle");

        Assert.Equal("upstream", await second.Content.ReadAsStringAsync());
        Assert.Equal("upstream", File.ReadAllText(assets.CachePath("nested/sample.bundle")));
        Assert.Single(assets.UpstreamRequests);
        Assert.Empty(assets.CacheTemporaryFiles());
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneCacheDownload()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var assets = new AssetHost(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Response("complete");
        });

        var first = assets.Client.GetAsync("/asset/Android/sample.bundle");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = assets.Client.GetAsync("/asset/Android/sample.bundle");
        release.SetResult();
        var responses = await Task.WhenAll(first, second);
        try
        {
            foreach (var response in responses)
                Assert.Equal("complete", await response.Content.ReadAsStringAsync());
            Assert.Single(assets.UpstreamRequests);
            Assert.Equal("complete", File.ReadAllText(assets.CachePath("sample.bundle")));
            Assert.Empty(assets.CacheTemporaryFiles());
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Fact]
    public async Task HeadRequestsForwardTheMethodWithoutPopulatingTheCache()
    {
        using var assets = new AssetHost();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/asset/Android/sample.bundle");

        using var response = await assets.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpMethod.Head, Assert.Single(assets.UpstreamRequests).Method);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.False(File.Exists(assets.CachePath("sample.bundle")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeaderTimeoutReturnsGatewayTimeout(bool headOnly)
    {
        using var assets = new AssetHost(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return Response("upstream");
        }, timeoutSeconds: 1);
        using var request = new HttpRequestMessage(headOnly ? HttpMethod.Head : HttpMethod.Get,
            "/asset/Android/sample.bundle");

        using var response = await assets.Client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        if (headOnly)
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.False(File.Exists(assets.CachePath("sample.bundle")));
        Assert.Empty(assets.CacheTemporaryFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyTimeoutBeforeResponseStartsReturnsGatewayTimeoutAndRemovesTemporaryFiles(bool cacheEnabled)
    {
        using var assets = new AssetHost((_, _) => Task.FromResult(DelayedResponse()),
            cacheEnabled: cacheEnabled, timeoutSeconds: 1);

        using var response = await assets.Client.GetAsync("/asset/Android/sample.bundle")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("official asset upstream timed out", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(assets.CachePath("sample.bundle")));
        Assert.Empty(assets.CacheTemporaryFiles());
    }

    [Fact]
    public async Task HeaderAndBodyShareOneTimeoutBudget()
    {
        using var assets = new AssetHost(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
            return DelayedResponse(delay: TimeSpan.FromMilliseconds(700));
        }, timeoutSeconds: 1);

        using var response = await assets.Client.GetAsync("/asset/Android/sample.bundle")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.False(File.Exists(assets.CachePath("sample.bundle")));
        Assert.Empty(assets.CacheTemporaryFiles());
    }

    [Fact]
    public async Task BodyTimeoutAfterResponseStartsAbortsTheResponseAndRemovesTemporaryFiles()
    {
        using var assets = new AssetHost((_, _) => Task.FromResult(DelayedResponse(firstChunkLength: 1)),
            timeoutSeconds: 1);
        using var response = await assets.Client.GetAsync("/asset/Android/sample.bundle",
            HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var error = await Record.ExceptionAsync(() => response.Content.ReadAsByteArrayAsync()
            .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(error is IOException or HttpRequestException or OperationCanceledException,
            error?.ToString() ?? "The response completed without an error.");
        Assert.False(File.Exists(assets.CachePath("sample.bundle")));
        Assert.Empty(assets.CacheTemporaryFiles());
    }

    [Fact]
    public async Task NoStoreResponsesAreNotCached()
    {
        using var assets = new AssetHost((_, _) =>
        {
            var response = Response("upstream");
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });

        using var response = await assets.Client.GetAsync("/asset/Android/sample.bundle");

        Assert.Equal("upstream", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(assets.CachePath("sample.bundle")));
        Assert.Empty(assets.CacheTemporaryFiles());
    }

    [Fact]
    public async Task ShortResponsesDoNotCreateCompletedCacheFiles()
    {
        using var assets = new AssetHost((_, _) =>
        {
            var response = Response("short");
            response.Content.Headers.ContentLength = 10;
            return Task.FromResult(response);
        });

        var response = await assets.Client.GetAsync("/asset/Android/sample.bundle",
            HttpCompletionOption.ResponseHeadersRead);
        using (response)
            await Record.ExceptionAsync(() => response.Content.ReadAsByteArrayAsync());

        Assert.False(File.Exists(assets.CachePath("sample.bundle")));
        Assert.Empty(assets.CacheTemporaryFiles());
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    private static HttpResponseMessage DelayedResponse(int firstChunkLength = 0, TimeSpan? delay = null)
    {
        var content = new StreamContent(new DelayedBodyStream(Encoding.UTF8.GetBytes("abcdef"),
            firstChunkLength, delay ?? TimeSpan.FromSeconds(30)));
        content.Headers.ContentLength = 6;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed record UpstreamRequest(HttpMethod Method, string? Range, string? IfRange);

    private sealed class AssetHost : IDisposable
    {
        private readonly IHost _host;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "static-assets-" + Guid.NewGuid().ToString("N"));
        public string LocalRoot => Path.Combine(Root, "local");
        private string CacheRoot => Path.Combine(Root, "cache");
        public ConcurrentQueue<UpstreamRequest> UpstreamRequests { get; } = new();
        public TestServer Server { get; }
        public HttpClient Client { get; }

        public AssetHost(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null,
            bool cacheEnabled = true,
            bool upstreamEnabled = true,
            int timeoutSeconds = 10)
        {
            Directory.CreateDirectory(Root);
            ServerConfig.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Game:BasicAuthUser"] = "",
                ["Game:BasicAuthPass"] = "",
                ["Static:OfficialAssetRootUrl"] = "https://assets.example.invalid/",
                ["Static:OfficialAssetRequestTimeoutSeconds"] = timeoutSeconds.ToString(),
                ["Static:LocalDirectory"] = LocalRoot,
                ["Static:CacheEnabled"] = cacheEnabled.ToString(),
                ["Static:CacheDirectory"] = CacheRoot,
                ["Static:UpstreamEnabled"] = upstreamEnabled.ToString()
            }).Build());
            var handler = new UpstreamHandler(async (request, cancellationToken) =>
            {
                UpstreamRequests.Enqueue(new UpstreamRequest(request.Method,
                    request.Headers.Range?.ToString(), request.Headers.IfRange?.ToString()));
                return respond == null ? Response("upstream") : await respond(request, cancellationToken);
            });
            _host = new HostBuilder()
                .UseContentRoot(Root)
                .ConfigureWebHost(builder => builder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddLogging();
                        services.AddRouting();
                        services.AddHttpClient("official-assets", client => client.Timeout = Timeout.InfiniteTimeSpan)
                            .ConfigurePrimaryHttpMessageHandler(() => handler);
                        services.AddSingleton<StaticAssetService>();
                    })
                    .Configure(app => app.Run(context =>
                    {
                        const string prefix = "/asset/Android/";
                        var path = context.Request.Path.Value![prefix.Length..];
                        return context.RequestServices.GetRequiredService<StaticAssetService>()
                            .ProxyAndroidAssetAsync(context, path, HttpMethods.IsHead(context.Request.Method));
                    })))
                .Start();
            Server = _host.GetTestServer();
            Client = _host.GetTestClient();
        }

        public void WriteLocal(string path, string body) => Write(Path.Combine(LocalRoot, "Android", path), body);
        public void WriteCache(string path, string body) => Write(CachePath(path), body);
        public string CachePath(string path) => Path.Combine(CacheRoot, "asset", "Android", path);
        public string[] CacheTemporaryFiles() => Directory.Exists(CacheRoot)
            ? Directory.GetFiles(CacheRoot, "*.tmp", SearchOption.AllDirectories)
            : [];

        private static void Write(string path, string body)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
        }

        public void Dispose()
        {
            Client.Dispose();
            _host.Dispose();
            Directory.Delete(Root, true);
        }
    }

    private sealed class UpstreamHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class DelayedBodyStream(byte[] bytes, int firstChunkLength, TimeSpan delay) : Stream
    {
        private int _position;
        private bool _delayed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == bytes.Length)
                return 0;
            if (_position >= firstChunkLength && !_delayed)
            {
                _delayed = true;
                await Task.Delay(delay, cancellationToken);
            }

            var end = _position < firstChunkLength ? firstChunkLength : bytes.Length;
            var count = Math.Min(buffer.Length, end - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> SkipReason = new(GetSkipReason);

    public SymbolicLinkFactAttribute() => Skip = SkipReason.Value;

    private static string? GetSkipReason()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var root = Path.Combine(Path.GetTempPath(), "symbolic-link-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(root, "directory");
            var file = Path.Combine(root, "file");
            Directory.CreateDirectory(directory);
            File.WriteAllText(file, "sample");
            Directory.CreateSymbolicLink(Path.Combine(root, "directory-link"), directory);
            File.CreateSymbolicLink(Path.Combine(root, "file-link"), file);
            return null;
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 1314)
        {
            return "Windows symbolic link creation requires Developer Mode or the Create symbolic links privilege.";
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
