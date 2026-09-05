using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using PocketBridge.Core;

namespace PocketBridge.Relay;

public sealed class ShortcutRoom(
    string id, byte[] receiverHash, byte[] senderHash,
    DateTimeOffset expiresAt, TimeSpan activeLifetime)
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stopped = new();
    private readonly TaskCompletionSource<WebSocket> receiverReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<TransferAck> acknowledgements = Channel.CreateBounded<TransferAck>(new BoundedChannelOptions(2)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly SemaphoreSlim uploadGate = new(1, 1);
    private DateTimeOffset expiration = expiresAt;
    private WebSocket? receiver;
    private bool receiverReserved;
    private bool ended;
    private int uploadCount;

    public string Id { get; } = id;
    public CancellationToken Stopped => stopped.Token;
    public bool IsExpired { get { lock (gate) return ended || DateTimeOffset.UtcNow >= expiration; } }

    public ShortcutRoomReservation? ReserveReceiver(byte[] tokenHash, out int status)
    {
        status = StatusCodes.Status404NotFound;
        if (!CryptographicOperations.FixedTimeEquals(receiverHash, tokenHash)) return null;
        lock (gate)
        {
            if (ended || DateTimeOffset.UtcNow >= expiration) return null;
            if (receiverReserved)
            {
                status = StatusCodes.Status409Conflict;
                return null;
            }
            receiverReserved = true;
            status = StatusCodes.Status101SwitchingProtocols;
            return new ShortcutRoomReservation(this);
        }
    }

    public bool AuthenticateSender(byte[] tokenHash)
    {
        if (!CryptographicOperations.FixedTimeEquals(senderHash, tokenHash)) return false;
        lock (gate) return !ended && DateTimeOffset.UtcNow < expiration;
    }

    internal bool AttachReceiver(WebSocket socket)
    {
        lock (gate)
        {
            if (ended || DateTimeOffset.UtcNow >= expiration)
            {
                socket.Abort();
                return false;
            }
            receiver = socket;
            expiration = DateTimeOffset.UtcNow.Add(activeLifetime);
            receiverReady.TrySetResult(socket);
            return true;
        }
    }

    public async Task RunReceiverAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[]? packet = await Wire.ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                if (packet is null) return;
                var message = ShortcutWire.Decode(packet);
                if (message.Type != Wire.Ack || message.Payload.Length > 16 * 1024) return;
                var ack = JsonSerializer.Deserialize<TransferAck>(message.Payload, Wire.Json);
                if (ack is null) return;
                await acknowledgements.Writer.WriteAsync(ack, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Stop(); }
    }

    public async Task<TransferAck> UploadAsync(Stream body, ShortcutUploadMetadata metadata, CancellationToken requestAborted)
    {
        if (!await uploadGate.WaitAsync(0, requestAborted).ConfigureAwait(false))
            throw new ShortcutUploadException(StatusCodes.Status409Conflict, "Another file is being uploaded to this connection.");
        bool announced = false;
        try
        {
            if (Volatile.Read(ref uploadCount) >= Wire.MaxTransfersPerSession)
                throw new ShortcutUploadException(StatusCodes.Status409Conflict, "Create a new QR after 10,000 files.");
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, stopped.Token);
            lifetime.CancelAfter(TimeSpan.FromHours(12));
            WebSocket socket;
            try { socket = await receiverReady.Task.WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token).ConfigureAwait(false); }
            catch (TimeoutException) { throw new ShortcutUploadException(StatusCodes.Status408RequestTimeout, "The Windows receiver is not connected."); }
            Interlocked.Increment(ref uploadCount);

            string transferId = Guid.NewGuid().ToString();
            var manifest = new TransferManifest(transferId, metadata.Name, metadata.OriginalSize, metadata.PayloadSize, metadata.Compression, metadata.Sha256);
            await ShortcutWire.SendJsonAsync(socket, Wire.Manifest, manifest, lifetime.Token).ConfigureAwait(false);
            announced = true;
            await ExpectAckAsync("ready", transferId, lifetime.Token).ConfigureAwait(false);

            byte[] buffer = new byte[Wire.ChunkSize];
            long total = 0;
            while (true)
            {
                int read = await body.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > metadata.PayloadSize)
                    throw new InvalidDataException("The HTTP body is larger than the declared payload.");
                byte[] packet = ShortcutWire.Encode(Wire.Chunk, buffer.AsSpan(0, read));
                await socket.SendAsync(packet, WebSocketMessageType.Binary, true, lifetime.Token).ConfigureAwait(false);
            }
            if (total != metadata.PayloadSize) throw new InvalidDataException("The HTTP body is shorter than the declared payload.");
            await ShortcutWire.SendJsonAsync(socket, Wire.End, new TransferEnd(transferId), lifetime.Token).ConfigureAwait(false);
            return await ExpectAckAsync("complete", transferId, lifetime.Token).ConfigureAwait(false);
        }
        catch (ShortcutUploadException) { throw; }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            if (announced) Stop();
            throw;
        }
        catch (Exception error)
        {
            if (announced) Stop();
            throw new ShortcutUploadException(StatusCodes.Status422UnprocessableEntity, error.Message, error);
        }
        finally { uploadGate.Release(); }
    }

    private async Task<TransferAck> ExpectAckAsync(string kind, string transferId, CancellationToken cancellationToken)
    {
        TransferAck ack = await acknowledgements.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(ack.TransferId, out var actual) || !Guid.TryParse(transferId, out var expected) || actual != expected)
            throw new InvalidDataException("The Windows receiver returned an acknowledgement for a different file.");
        if (ack.Kind == "error") throw new InvalidDataException(ack.Message ?? "Windows could not save the file.");
        if (ack.Kind != kind) throw new InvalidDataException("The Windows acknowledgement order is invalid.");
        return ack;
    }

    public void Stop()
    {
        lock (gate)
        {
            if (ended) return;
            ended = true;
            stopped.Cancel();
            acknowledgements.Writer.TryComplete();
            receiver?.Abort();
        }
    }
}

public sealed class ShortcutRoomReservation(ShortcutRoom room) : IDisposable
{
    public ShortcutRoom Room { get; } = room;
    public bool Attach(WebSocket socket) => Room.AttachReceiver(socket);
    public void Dispose() => Room.Stop();
}

public sealed class ShortcutUploadException(int statusCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int StatusCode { get; } = statusCode;
}
