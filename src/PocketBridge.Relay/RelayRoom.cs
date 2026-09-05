using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace PocketBridge.Relay;

public sealed class RelayRoom(
    string id, byte[] receiverHash, byte[] senderHash,
    DateTimeOffset expiresAt, TimeSpan activeLifetime)
{
    public const int MaximumMessageBytes = 1024 * 1024;
    private readonly object gate = new();
    private readonly CancellationTokenSource stopped = new();
    private readonly TaskCompletionSource paired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, WebSocket?> sockets = new(StringComparer.Ordinal);
    private DateTimeOffset expiration = expiresAt;
    private bool ended;

    public string Id { get; } = id;
    public CancellationToken Stopped => stopped.Token;
    public bool IsExpired { get { lock (gate) return ended || DateTimeOffset.UtcNow >= expiration; } }

    public RoomReservation? Reserve(string role, byte[] tokenHash, out int status)
    {
        status = StatusCodes.Status404NotFound;
        var expected = role == "receiver" ? receiverHash : senderHash;
        if (!CryptographicOperations.FixedTimeEquals(expected, tokenHash)) return null;
        lock (gate)
        {
            if (ended || DateTimeOffset.UtcNow >= expiration) return null;
            if (sockets.ContainsKey(role))
            {
                status = StatusCodes.Status409Conflict;
                return null;
            }
            sockets.Add(role, null);
            status = StatusCodes.Status101SwitchingProtocols;
            return new RoomReservation(this, role);
        }
    }

    internal bool Attach(string role, WebSocket socket)
    {
        lock (gate)
        {
            if (ended || DateTimeOffset.UtcNow >= expiration)
            {
                socket.Abort();
                return false;
            }
            sockets[role] = socket;
            if (sockets.Count == 2 && sockets.Values.All(s => s is not null))
            {
                expiration = DateTimeOffset.UtcNow.Add(activeLifetime);
                paired.TrySetResult();
            }
            return true;
        }
    }

    public async Task ForwardAsync(string role, WebSocket source, CancellationToken cancellationToken)
    {
        // Each received message is bounded. Forwarding its fragments uses only 64 KiB per direction.
        // Awaited sends provide backpressure; exactly one forwarder sends to a given peer.
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var messageBytes = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(buffer.AsMemory(0, 64 * 1024), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Binary) return;
                messageBytes += result.Count;
                if (messageBytes > MaximumMessageBytes) return;
                await paired.Task.WaitAsync(cancellationToken);
                WebSocket peer;
                lock (gate)
                {
                    if (ended) return;
                    peer = sockets[role == "sender" ? "receiver" : "sender"]!;
                }
                await peer.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Binary,
                    result.EndOfMessage, cancellationToken);
                if (result.EndOfMessage) messageBytes = 0;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (ended) return;
            ended = true;
            stopped.Cancel();
            foreach (var socket in sockets.Values) socket?.Abort();
        }
    }
}

public sealed class RoomReservation(RelayRoom room, string role) : IDisposable
{
    public RelayRoom Room { get; } = room;
    public string Role { get; } = role;
    public bool Attach(WebSocket socket) => Room.Attach(Role, socket);
    public void Dispose() => Room.Stop();
}
