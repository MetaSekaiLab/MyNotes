using Microsoft.AspNetCore.StaticFiles;
using MyNotes.Config;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security;

namespace MyNotes.Services;

public sealed class StaticAssetService
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly char[] InvalidCacheFileNameChars = ['<', '>', ':', '"', '|', '?', '*', '\0'];
    private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly ILogger<StaticAssetService> _logger;
    private readonly HttpClient _officialAssets;
    private readonly string? _localRoot;
    private readonly string? _cacheRoot;
    private readonly ConcurrentDictionary<string, CacheLockEntry> _cacheLocks = new(StringComparer.OrdinalIgnoreCase);

    public StaticAssetService(
        ILogger<StaticAssetService> logger,
        IHttpClientFactory httpClientFactory,
        IHostEnvironment environment)
    {
        _logger = logger;
        _officialAssets = httpClientFactory.CreateClient("official-assets");
        _localRoot = string.IsNullOrWhiteSpace(ServerConfig.StaticAssetLocalDirectory)
            ? null
            : Path.GetFullPath(ServerConfig.StaticAssetLocalDirectory, environment.ContentRootPath);
        _cacheRoot = ServerConfig.StaticAssetCacheEnabled
            ? ResolveCacheRoot(environment.ContentRootPath)
            : null;

        if (_cacheRoot != null)
            _logger.LogInformation("Static asset cache enabled at {CacheRoot}", _cacheRoot);
    }

    public async Task ProxyAndroidAssetAsync(HttpContext context, string assetPath, bool headOnly)
    {
        var normalizedPath = NormalizeAssetPath(assetPath);
        if (normalizedPath == null)
        {
            await WriteBadRequestAsync(context);
            return;
        }

        var officialAssetPath = $"Android/{normalizedPath}";
        var localPath = _localRoot == null ? null : ResolveSafeFilePath(_localRoot, officialAssetPath);
        if (localPath != null && File.Exists(localPath))
        {
            await ServeFileAsync(context, localPath);
            return;
        }

        var cachePath = IsCatalogMarker(normalizedPath) ? null : GetCachePath(officialAssetPath);
        if (cachePath != null && File.Exists(cachePath))
        {
            await ServeFileAsync(context, cachePath);
            return;
        }

        if (!ServerConfig.StaticAssetUpstreamEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (cachePath == null || headOnly || context.Request.Headers.ContainsKey("Range"))
        {
            await ProxyOfficialAssetAsync(context, officialAssetPath, headOnly, null);
            return;
        }

        var cacheLockPath = cachePath;
        var cacheLock = await AcquireCacheLockAsync(cacheLockPath, context.RequestAborted);
        try
        {
            cachePath = GetCachePath(officialAssetPath);
            if (cachePath == null)
            {
                await ProxyOfficialAssetAsync(context, officialAssetPath, false, null);
                return;
            }

            if (!File.Exists(cachePath))
            {
                await ProxyOfficialAssetAsync(context, officialAssetPath, false, cachePath);
                return;
            }
        }
        finally
        {
            ReleaseCacheLock(cacheLockPath, cacheLock);
        }

        await ServeFileAsync(context, cachePath);
    }

    private async Task ProxyOfficialAssetAsync(
        HttpContext context,
        string assetPath,
        bool headOnly,
        string? cachePath)
    {
        var upstreamUrl = BuildOfficialAssetUrl(assetPath);
        if (upstreamUrl == null)
        {
            await WriteProxyErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                "official asset root is not configured", headOnly);
            return;
        }

        _logger.LogInformation("Proxying asset {AssetPath} from {UpstreamUrl}", assetPath, upstreamUrl);

        using var upstreamRequest = new HttpRequestMessage(headOnly ? HttpMethod.Head : HttpMethod.Get, upstreamUrl);
        if (ServerConfig.HasBasicAuth)
            upstreamRequest.Headers.TryAddWithoutValidation("AUTHORIZATION", ServerConfig.ExpectedBasicAuth);
        upstreamRequest.Headers.TryAddWithoutValidation("User-Agent", "MyNotes/1.0");
        upstreamRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        if (context.Request.Headers.TryGetValue("Range", out var range))
            upstreamRequest.Headers.TryAddWithoutValidation("Range", range.ToString());
        if (context.Request.Headers.TryGetValue("If-Range", out var ifRange))
            upstreamRequest.Headers.TryAddWithoutValidation("If-Range", ifRange.ToString());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(ServerConfig.OfficialAssetRequestTimeoutSeconds));
        try
        {
            using var upstreamResponse = await _officialAssets.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            context.Response.Headers["X-Accel-Buffering"] = "no";
            CopyHeaders(context, upstreamResponse.Headers);
            CopyHeaders(context, upstreamResponse.Content.Headers);

            if (!upstreamResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Official asset proxy returned {StatusCode} for {UpstreamUrl}",
                    (int)upstreamResponse.StatusCode,
                    upstreamUrl);
            }

            if (!headOnly)
            {
                var upstreamBody = await upstreamResponse.Content.ReadAsStreamAsync(timeout.Token);
                if (cachePath != null && IsCacheable(upstreamResponse))
                {
                    if (await CopyAndCacheAsync(
                            upstreamBody,
                            context.Response.Body,
                            cachePath,
                            upstreamResponse.Content.Headers.ContentLength,
                            timeout.Token))
                    {
                        _logger.LogInformation("Cached static asset at {CachePath}", cachePath);
                    }
                }
                else
                {
                    await upstreamBody.CopyToAsync(context.Response.Body, timeout.Token);
                }
            }
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested &&
            (ex is OperationCanceledException ||
             timeout.IsCancellationRequested && (ex is HttpRequestException or IOException)))
        {
            _logger.LogWarning("Official asset proxy timed out for {UpstreamUrl}", upstreamUrl);
            await WriteProxyErrorAsync(context, StatusCodes.Status504GatewayTimeout,
                "official asset upstream timed out", headOnly);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logger.LogWarning(ex, "Official asset proxy failed for {UpstreamUrl}", upstreamUrl);
            await WriteProxyErrorAsync(context, StatusCodes.Status502BadGateway,
                "official asset upstream failed", headOnly);
        }
    }

    private async Task ServeFileAsync(HttpContext context, string filePath)
    {
        if (!ContentTypes.TryGetContentType(filePath, out var contentType))
            contentType = "application/octet-stream";

        _logger.LogInformation("Serving static asset from file {FilePath}", filePath);
        await Results.File(
                filePath,
                contentType,
                lastModified: File.GetLastWriteTimeUtc(filePath),
                enableRangeProcessing: true)
            .ExecuteAsync(context);
    }

    private static async Task WriteProxyErrorAsync(
        HttpContext context, int statusCode, string message, bool headOnly)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        if (!headOnly)
            await context.Response.WriteAsync(message, context.RequestAborted);
    }

    private async Task<bool> CopyAndCacheAsync(
        Stream source,
        Stream? responseBody,
        string cachePath,
        long? expectedLength,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        FileStream? cacheStream = null;

        try
        {
            try
            {
                var directory = Path.GetDirectoryName(cachePath)!;
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(
                    directory,
                    $".{Path.GetFileName(cachePath)}.{Guid.NewGuid():N}.tmp");
                cacheStream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception ex) when (IsCacheStorageException(ex))
            {
                _logger.LogWarning(ex, "Could not create static asset cache file {CachePath}", cachePath);
                if (responseBody != null)
                    await source.CopyToAsync(responseBody, cancellationToken);
                return false;
            }

            var buffer = new byte[81920];
            var cacheWritable = true;
            long bytesCopied = 0;
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                    break;

                bytesCopied += bytesRead;

                if (responseBody != null)
                    await responseBody.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                if (!cacheWritable)
                    continue;

                try
                {
                    await cacheStream!.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }
                catch (Exception ex) when (IsCacheStorageException(ex))
                {
                    _logger.LogWarning(ex, "Could not write static asset cache file {CachePath}", cachePath);
                    cacheWritable = false;
                    await DisposeCacheStreamAsync(cacheStream!);
                    cacheStream = null;
                    DeleteTemporaryFile(temporaryPath!);
                    temporaryPath = null;

                    if (responseBody == null)
                        return false;
                }
            }

            if (!cacheWritable)
                return false;

            if (expectedLength.HasValue && bytesCopied != expectedLength.Value)
            {
                _logger.LogWarning(
                    "Static asset body length mismatch for {CachePath}: expected {ExpectedLength}, received {ActualLength}",
                    cachePath,
                    expectedLength.Value,
                    bytesCopied);
                return false;
            }

            try
            {
                var completedStream = cacheStream!;
                await completedStream.FlushAsync(cancellationToken);
                await completedStream.DisposeAsync();
                cacheStream = null;

                File.Move(temporaryPath!, cachePath, true);
                temporaryPath = null;
                return true;
            }
            catch (Exception ex) when (IsCacheStorageException(ex))
            {
                _logger.LogWarning(ex, "Could not finalize static asset cache file {CachePath}", cachePath);
                return false;
            }
        }
        finally
        {
            if (cacheStream != null)
                await DisposeCacheStreamAsync(cacheStream);
            if (temporaryPath != null)
                DeleteTemporaryFile(temporaryPath);
        }
    }

    private async Task<CacheLockEntry> AcquireCacheLockAsync(
        string cachePath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var entry = _cacheLocks.GetOrAdd(cachePath, static _ => new CacheLockEntry());
            if (!entry.TryAddReference())
            {
                RemoveCacheLock(cachePath, entry);
                continue;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken);
                return entry;
            }
            catch
            {
                ReleaseCacheLockReference(cachePath, entry);
                throw;
            }
        }
    }

    private void ReleaseCacheLock(string cachePath, CacheLockEntry entry)
    {
        entry.Semaphore.Release();
        ReleaseCacheLockReference(cachePath, entry);
    }

    private void ReleaseCacheLockReference(string cachePath, CacheLockEntry entry)
    {
        if (!entry.ReleaseReference())
            return;

        RemoveCacheLock(cachePath, entry);
        entry.Semaphore.Dispose();
    }

    private void RemoveCacheLock(string cachePath, CacheLockEntry entry) =>
        ((ICollection<KeyValuePair<string, CacheLockEntry>>)_cacheLocks)
            .Remove(new KeyValuePair<string, CacheLockEntry>(cachePath, entry));

    private string? GetCachePath(string assetPath)
    {
        if (_cacheRoot == null)
            return null;

        return ResolveSafeFilePath(_cacheRoot, $"asset/{assetPath}");
    }

    private string? ResolveSafeFilePath(string root, string assetPath)
    {
        try
        {
            var segments = assetPath.Split('/');
            if (segments.Length == 0 || segments.Any(static segment => !IsCacheableFileName(segment)))
                return null;

            var fullPath = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
            var relativeToRoot = Path.GetRelativePath(root, fullPath);
            if (Path.IsPathFullyQualified(relativeToRoot) ||
                relativeToRoot == ".." ||
                relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                _logger.LogWarning("Static asset path escaped its configured root: {AssetPath}", assetPath);
                return null;
            }

            var currentPath = root;
            for (var ancestor = new DirectoryInfo(root); ancestor != null; ancestor = ancestor.Parent)
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    return null;
            foreach (var segment in new[] { "" }.Concat(segments))
            {
                if (segment.Length > 0)
                    currentPath = Path.Combine(currentPath, segment);

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(currentPath);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    break;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    _logger.LogWarning("Static asset path contains a symbolic link: {AssetPath}", assetPath);
                    return null;
                }
            }

            return fullPath;
        }
        catch (Exception ex) when (IsCacheStorageException(ex) || ex is ArgumentException)
        {
            _logger.LogWarning(ex, "Could not resolve static asset path for {AssetPath}", assetPath);
            return null;
        }
    }

    private static string ResolveCacheRoot(string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(ServerConfig.StaticAssetCacheDirectory))
            throw new InvalidOperationException("Static:CacheDirectory must be set when static asset caching is enabled");

        return Path.GetFullPath(ServerConfig.StaticAssetCacheDirectory, contentRootPath);
    }

    private static bool IsCacheable(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.OK &&
        response.Headers.CacheControl?.NoStore != true &&
        response.Content.Headers.ContentRange == null &&
        response.Content.Headers.ContentEncoding.Count == 0;

    private static bool IsCacheableFileName(string segment)
    {
        if (segment.Length == 0 ||
            segment.EndsWith('.') ||
            segment.EndsWith(' ') ||
            segment.IndexOfAny(InvalidCacheFileNameChars) >= 0 ||
            segment.Any(char.IsControl))
            return false;

        var baseName = segment.Split('.')[0];
        return !ReservedWindowsFileNames.Contains(baseName);
    }

    private static bool IsCacheStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException;

    private static async Task DisposeCacheStreamAsync(FileStream stream)
    {
        try
        {
            await stream.DisposeAsync();
        }
        catch (Exception ex) when (IsCacheStorageException(ex))
        {
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsCacheStorageException(ex))
        {
        }
    }

    private static Task WriteBadRequestAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsync("invalid asset path", context.RequestAborted);
    }

    private static string? NormalizeAssetPath(string assetPath)
    {
        var normalizedPath = assetPath.Replace('\\', '/');
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalizedPath.StartsWith('/') ||
            Path.IsPathRooted(normalizedPath) ||
            segments.Length == 0 ||
            segments.Any(static s => s is "." or ".." || !IsCacheableFileName(s)))
            return null;

        return string.Join('/', segments);
    }

    private static bool IsCatalogMarker(string assetPath)
    {
        var fileName = assetPath[(assetPath.LastIndexOf('/') + 1)..];
        return fileName.StartsWith("catalog", StringComparison.OrdinalIgnoreCase) &&
            (!fileName.Contains('.') ||
             fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
             fileName.EndsWith(".hash", StringComparison.OrdinalIgnoreCase));
    }

    private static Uri? BuildOfficialAssetUrl(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(ServerConfig.OfficialAssetRootUrl))
            return null;

        var root = ServerConfig.OfficialAssetRootUrl.TrimEnd('/') + "/";
        if (!Uri.TryCreate(root, UriKind.Absolute, out var rootUri) ||
            rootUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(rootUri.Query) ||
            !string.IsNullOrEmpty(rootUri.Fragment))
            return null;

        var escapedAssetPath = string.Join(
            '/',
            assetPath.Split('/').Select(Uri.EscapeDataString));
        return new Uri(rootUri, $"asset/{escapedAssetPath}");
    }

    private static void CopyHeaders(HttpContext context, HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
                continue;

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private sealed class CacheLockEntry
    {
        private readonly object _sync = new();
        private int _referenceCount;
        private bool _retired;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool TryAddReference()
        {
            lock (_sync)
            {
                if (_retired)
                    return false;

                _referenceCount++;
                return true;
            }
        }

        public bool ReleaseReference()
        {
            lock (_sync)
            {
                _referenceCount--;
                if (_referenceCount != 0)
                    return false;

                _retired = true;
                return true;
            }
        }
    }
}
