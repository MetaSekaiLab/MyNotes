using App.Protobuf.Entity;
using App.Protobuf.Home;
using App.Protobuf.Player;
using Google.Protobuf;

namespace MyNotes.Services;

internal static class ClientStateProtocol
{
    private const int MemoryStatusField = 49;
    private const int VipField = 50;
    private const int TutorialProgressField = 53;
    private const int PlayerPersonalNotificationField = 60;
    private const int AccountIdField = 6;
    private const int HomePersonalNotificationField = 10;

    public static void InitializePlayer(PlayerData data) => InitializeMessages(data,
        MemoryStatusField, VipField, TutorialProgressField, PlayerPersonalNotificationField);

    public static void SetAccountId(GetPlayerDataResponse response, long accountId)
    {
        MergeFields(response, output =>
        {
            output.WriteTag(AccountIdField, WireFormat.WireType.Varint);
            output.WriteInt64(accountId);
        });
    }

    public static void InitializeHome(GetHomeResponse response) =>
        InitializeMessages(response, HomePersonalNotificationField);

    private static void InitializeMessages(IMessage message, params int[] fields) =>
        MergeFields(message, output =>
        {
            foreach (var field in fields)
            {
                output.WriteTag(field, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.Empty);
            }
        });

    // Extension fields use the message's unknown-field storage, which preserves them through cloning and parsing.
    private static void MergeFields(IMessage message, Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
            write(output);
        message.MergeFrom(stream.ToArray());
    }
}
