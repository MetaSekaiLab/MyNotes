namespace MyNotes.Config;

public static class ServerConfig
{
    public static int GrpcPort { get; private set; } = 9831;
    public static int HttpPort { get; private set; } = 9832;
    public static string CertificatePath { get; private set; } = "";
    public static string CertificatePassword { get; private set; } = "";
    public static string MasterVersion { get; private set; } = "9c23d88a191e4648f0a28f4bbd4edb6403d5adf99ac5793efd7cc1cb3d49104c";
    public static string InitialDataGroup { get; private set; } = "cbt_0.1.8_";
    public static IReadOnlyList<long> InitialStampIds { get; private set; } = [];
    public static string BasicAuthUser { get; private set; } = "";
    public static string BasicAuthPass { get; private set; } = "";
    public static bool RequireBasicAuth { get; private set; }
    public static string DataDirectory { get; private set; } = "data";
    public static string OfficialAssetRootUrl { get; private set; } = "";
    public static int OfficialAssetRequestTimeoutSeconds { get; private set; } = 30;
    public static bool StaticAssetCacheEnabled { get; private set; }
    public static string StaticAssetCacheDirectory { get; private set; } = "data/static-cache";
    public static string StaticAssetLocalDirectory { get; private set; } = "";
    public static bool StaticAssetUpstreamEnabled { get; private set; } = true;
    public static string ResourceReleaseManifest { get; private set; } = "";
    public static string ResourceVersion { get; private set; } = "";

    public static void Load(IConfiguration configuration)
    {
        GrpcPort = configuration.GetValue("Server:GrpcPort", configuration.GetValue("Server:Port", GrpcPort));
        HttpPort = configuration.GetValue("Server:HttpPort", HttpPort);
        CertificatePath = configuration.GetValue("Server:CertificatePath", "") ?? "";
        CertificatePassword = configuration.GetValue("Server:CertificatePassword", "") ?? "";
        MasterVersion = configuration.GetValue("Game:MasterVersion", MasterVersion) ?? MasterVersion;
        InitialDataGroup = configuration.GetValue("Game:InitialDataGroup", InitialDataGroup) ?? InitialDataGroup;
        InitialStampIds = configuration.GetSection("Game:InitialStampIds").Get<long[]>()?
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToArray() ?? [];
        BasicAuthUser = configuration.GetValue("Game:BasicAuthUser", BasicAuthUser) ?? BasicAuthUser;
        BasicAuthPass = configuration.GetValue("Game:BasicAuthPass", BasicAuthPass) ?? BasicAuthPass;
        RequireBasicAuth = configuration.GetValue("Game:RequireBasicAuth", RequireBasicAuth);
        DataDirectory = configuration.GetValue("Game:DataDirectory", DataDirectory) ?? DataDirectory;
        OfficialAssetRootUrl = configuration.GetValue("Static:OfficialAssetRootUrl", OfficialAssetRootUrl) ?? OfficialAssetRootUrl;
        OfficialAssetRequestTimeoutSeconds = configuration.GetValue(
            "Static:OfficialAssetRequestTimeoutSeconds",
            OfficialAssetRequestTimeoutSeconds);
        StaticAssetCacheEnabled = configuration.GetValue(
            "Static:CacheEnabled",
            StaticAssetCacheEnabled);
        StaticAssetCacheDirectory = configuration.GetValue(
            "Static:CacheDirectory",
            StaticAssetCacheDirectory) ?? StaticAssetCacheDirectory;
        StaticAssetLocalDirectory = configuration.GetValue("Static:LocalDirectory", "") ?? "";
        StaticAssetUpstreamEnabled = configuration.GetValue("Static:UpstreamEnabled", true);
        ResourceReleaseManifest = configuration.GetValue("Resources:ReleaseManifest", "") ?? "";
        ResourceVersion = configuration.GetValue("Resources:ResourceVersion", "") ?? "";
        if (OfficialAssetRequestTimeoutSeconds <= 0)
            throw new InvalidOperationException("Static:OfficialAssetRequestTimeoutSeconds must be positive");
        if (RequireBasicAuth && !HasBasicAuth)
            throw new InvalidOperationException("Basic authentication requires a configured user and password");
    }

    public static bool HasBasicAuth =>
        !string.IsNullOrWhiteSpace(BasicAuthUser) &&
        !string.IsNullOrWhiteSpace(BasicAuthPass);

    public static string ExpectedBasicAuth =>
        "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{BasicAuthUser}:{BasicAuthPass}"));

    public static bool IsExpectedBasicAuth(string value) =>
        HasBasicAuth && string.Equals(value, ExpectedBasicAuth, StringComparison.Ordinal);
}
