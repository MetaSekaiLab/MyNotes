using Google.Protobuf;

namespace MyNotes.Services;

public sealed class FeatureSwitchService
{
    private readonly byte[] _response;

    public FeatureSwitchService(IConfiguration configuration)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            foreach (var section in configuration.GetSection("FeatureSwitches").GetChildren())
            {
                if (!bool.TryParse(section.Value, out var enabled))
                    throw new InvalidOperationException("Feature switches must have boolean values");
                using var entryStream = new MemoryStream();
                using (var entry = new CodedOutputStream(entryStream, leaveOpen: true))
                {
                    entry.WriteTag(1, WireFormat.WireType.LengthDelimited);
                    entry.WriteString(section.Key);
                    entry.WriteTag(2, WireFormat.WireType.Varint);
                    entry.WriteBool(enabled);
                }
                output.WriteTag(1, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(entryStream.ToArray()));
            }
        }
        _response = stream.ToArray();
    }

    public byte[] Get() => _response;
}
