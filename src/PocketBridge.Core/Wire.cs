using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace PocketBridge.Core;

public static class Wire
{
    public const int ChunkSize = 256 * 1024;
    public const int MaxMessageSize = 1024 * 1024;
    public const long MaxFileSize = 100L * 1024 * 1024 * 1024;
    public const int MaxTransfersPerSession = 10_000;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const byte Manifest = 1, Chunk = 2, End = 3, Ack = 4;

    public static byte[] Encrypt(byte type, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key)
    {
        if (payload.Length > MaxMessageSize - 30) throw new InvalidDataException("메시지가 너무 큽니다.");
        byte[] plain = new byte[payload.Length + 1];
        plain[0] = type;
        payload.CopyTo(plain.AsSpan(1));
        byte[] packet = new byte[plain.Length + 29];
        packet[0] = 1;
        RandomNumberGenerator.Fill(packet.AsSpan(1, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(packet.AsSpan(1, 12), plain, packet.AsSpan(13, plain.Length), packet.AsSpan(13 + plain.Length, 16));
        CryptographicOperations.ZeroMemory(plain);
        return packet;
    }

    public static (byte Type, byte[] Payload) Decrypt(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> key)
    {
        if (packet.Length < 30 || packet.Length > MaxMessageSize || packet[0] != 1)
            throw new InvalidDataException("지원하지 않는 전송 메시지입니다.");
        byte[] plain = new byte[packet.Length - 29];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(packet.Slice(1, 12), packet.Slice(13, plain.Length), packet[^16..], plain);
        var result = (plain[0], plain[1..]);
        CryptographicOperations.ZeroMemory(plain);
        return result;
    }

    public static async Task<byte[]?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        byte[] buffer = new byte[32 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("바이너리 메시지만 지원합니다.");
            if (message.Length + result.Count > MaxMessageSize) throw new InvalidDataException("메시지 크기 제한을 초과했습니다.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return message.ToArray();
        }
    }

    public static Task SendJsonAsync<T>(WebSocket socket, byte type, T value, byte[] key, CancellationToken cancellationToken) =>
        socket.SendAsync(new ArraySegment<byte>(Encrypt(type, JsonSerializer.SerializeToUtf8Bytes(value, Json), key)), WebSocketMessageType.Binary, true, cancellationToken);

    public static Uri ValidateServer(string server)
    {
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("중계 서버의 HTTPS 주소를 입력하세요. 예: https://relay.example.com (로컬 개발은 http://localhost:5080)");
        return uri;
    }

    public static Uri SocketUri(Uri server, string room, string role)
    {
        var builder = new UriBuilder(server) { Scheme = server.Scheme == "https" ? "wss" : "ws", Path = $"/ws/{Uri.EscapeDataString(room)}/{role}" };
        return builder.Uri;
    }
}
