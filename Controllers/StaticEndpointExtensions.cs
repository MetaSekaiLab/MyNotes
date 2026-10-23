using MyNotes.Services;

namespace MyNotes.Controllers;

public static class StaticEndpointExtensions
{
    public static IEndpointRouteBuilder MapStaticAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/Master/MasterDataSystemVersion.txt", MasterVersion);
        endpoints.MapGet("/MasterDataSystemVersion.txt", MasterVersion);

        MapAndroidAssetProxy(endpoints, "/asset/Android/{**assetPath}");
        MapAndroidAssetProxy(endpoints, "/Android/{**assetPath}");
        endpoints.MapMethods("/master/{version}/{fileName}", [HttpMethods.Get, HttpMethods.Head],
            (string version, string fileName, ResourceReleaseService release) =>
                release.FindMaster(version, fileName) is { } path
                    ? Results.File(path, "application/octet-stream", enableRangeProcessing: true)
                    : Results.NotFound());

        return endpoints;
    }

    private static IResult MasterVersion(MasterDataService master) =>
        Results.Text(master.Version, "text/plain");

    private static void MapAndroidAssetProxy(IEndpointRouteBuilder endpoints, string pattern)
    {
        endpoints.MapMethods(
            pattern,
            [HttpMethods.Get, HttpMethods.Head],
            async (HttpContext context, StaticAssetService assets, ResourceReleaseService release, string assetPath) =>
            {
                if (release.FindAndroid(assetPath) is { } path)
                    await Results.File(path, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(context);
                else if (release.Enabled && IsCatalogMarker(assetPath))
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                else
                    await assets.ProxyAndroidAssetAsync(context, assetPath, HttpMethods.IsHead(context.Request.Method));
            });
    }

    private static bool IsCatalogMarker(string assetPath)
    {
        var name = assetPath.Split('/').Last();
        return name.StartsWith("catalog", StringComparison.OrdinalIgnoreCase) &&
            (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith(".hash", StringComparison.OrdinalIgnoreCase) || !name.Contains('.'));
    }
}
