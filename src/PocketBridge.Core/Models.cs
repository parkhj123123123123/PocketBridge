using System.Text.Json;
namespace PocketBridge.Core;
public sealed record PairingInvite(int Version, string Server, string Room, string Token, string Key)
{
    public string ToJson() => JsonSerializer.Serialize(this, Wire.Json);
    public static PairingInvite Parse(string json) => JsonSerializer.Deserialize<PairingInvite>(json, Wire.Json) ?? throw new InvalidDataException("연결 정보를 읽을 수 없습니다.");
}

public sealed record ShortcutInvite(int Version, string Server, string Room, string Token)
{
    public string ToJson() => JsonSerializer.Serialize(this, Wire.Json);
    public static ShortcutInvite Parse(string json) => JsonSerializer.Deserialize<ShortcutInvite>(json, Wire.Json) ?? throw new InvalidDataException("단축어 연결 정보를 읽을 수 없습니다.");
}
public sealed record RelaySession(string RoomId, string ReceiverToken, string SenderToken, DateTimeOffset ExpiresAt);
public sealed record TransferManifest(string TransferId, string FileName, long OriginalSize, long PayloadSize, string Compression, string Sha256);
public sealed record TransferEnd(string TransferId);
public sealed record TransferAck(string Kind, string TransferId, string? FileName = null, string? Message = null);
public sealed record ReceivedFile(string Name, string FullPath, long Size, long WireSize, DateTimeOffset ReceivedAt, string Sha256);
public sealed record ReceiverUpdate(string State, string Message, string? FileName = null, long BytesReceived = 0, long TotalBytes = 0)
{
    public double Percent => TotalBytes > 0 ? Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100) : 0;
}
public static class Formatters
{
    public static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, value);
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }
}
