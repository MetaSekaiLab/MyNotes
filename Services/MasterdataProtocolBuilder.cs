using App.Protobuf.Masterdata;

namespace MyNotes.Services;

public sealed class MasterdataProtocolBuilder(MasterDataService masterData)
{
    public VersionResponse Version() => new()
    {
        Version = masterData.Version,
        ResourceVersion = masterData.ResourceVersion
    };
}
