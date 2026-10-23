using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using MyNotes.Middleware;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class PlayerLoginService
{
    private readonly PlayerManager _players;
    private readonly PlayerProtocolBuilder _protocol;
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerRecord> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly string[] _server;

    public PlayerLoginService(IConfiguration configuration, PlayerManager players, PlayerProtocolBuilder protocol)
    {
        _players = players;
        _protocol = protocol;
        foreach (var section in configuration.GetSection("Login:Accounts").GetChildren())
        {
            var uid = section["Uid"] ?? "";
            var hash = section["AccessTokenSha256"] ?? "";
            if (string.IsNullOrWhiteSpace(uid) || hash.Length != 64 || !hash.All(Uri.IsHexDigit) ||
                !_accounts.TryAdd(uid, new(Convert.FromHexString(hash), section["InitialDataGroup"] ?? "Default")))
                throw new InvalidOperationException("Login accounts require unique UIDs and SHA-256 token digests");
        }
        _server = [configuration["Login:Server:Name"] ?? "MyNotes",
            configuration["Login:Server:CdnRoot"] ?? "",
            configuration["Login:Server:ApiServerRoot"] ?? "",
            configuration["Login:Server:ChatServerRoot"] ?? "",
            configuration["Login:Server:AtServerRoot"] ?? "",
            configuration["Login:Server:LiveServer"] ?? "",
            configuration["Login:Server:DisplayName"] ?? "MyNotes",
            configuration["Login:Server:AreaId"] ?? "",
            configuration["Login:Server:AgeIconSpriteName"] ?? ""];
    }

    public byte[] ServerList() => Encode(output =>
        WriteBytes(output, 1, Encode(server =>
        {
            for (var index = 0; index < _server.Length; index++)
                if (_server[index].Length != 0)
                {
                    server.WriteTag(index + 1, WireFormat.WireType.LengthDelimited);
                    server.WriteString(_server[index]);
                }
        })));

    public byte[] PreLogin(ReadOnlyMemory<byte> payload)
    {
        _ = Authenticate(payload);
        return Encode(output => { output.WriteTag(1, WireFormat.WireType.Varint); output.WriteBool(true); });
    }

    public byte[] Login(ReadOnlyMemory<byte> payload)
    {
        var (uid, account) = Authenticate(payload);
        PlayerRecord player;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(uid, out player!))
                _sessions.Add(uid, player = _players.Register(account.InitialDataGroup));
        }
        return Encode(output =>
        {
            WriteBytes(output, 1, _protocol.Register(player).Credential!.ToByteArray());
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteString(_server[0]);
            output.WriteTag(3, WireFormat.WireType.LengthDelimited);
            output.WriteString(_server[6]);
        });
    }

    private (string Uid, Account Account) Authenticate(ReadOnlyMemory<byte> payload)
    {
        using var input = new CodedInputStream(payload.ToArray());
        string uid = "", token = "";
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == 10) uid = input.ReadString();
            else if (tag == 18) token = input.ReadString();
            else input.SkipLastField();
        }
        if (token.Length == 0 || !_accounts.TryGetValue(uid, out var account) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), account.TokenHash))
            throw new GrpcStatusException("16", "invalid login credential");
        return (uid, account);
    }

    private static byte[] Encode(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true)) write(output);
        return stream.ToArray();
    }

    private static void WriteBytes(CodedOutputStream output, int field, byte[] bytes)
    {
        output.WriteTag(field, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(bytes));
    }

    private sealed record Account(byte[] TokenHash, string InitialDataGroup);
}
