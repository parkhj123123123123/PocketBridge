using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace PocketBridge.Core;

/// <summary>A single QR pairing session. Dispose it to cancel transfer and discard incomplete files.</summary>
public sealed class ReceiverClient : IAsyncDisposable
{
    private readonly Uri _server;
    private readonly string _destination;
    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly TransferLedger _transfers = new();
    private CancellationTokenSource? _cancellation;
    private bool _started;
    private bool _disposed;
    private readonly object _lifecycle = new();
    private readonly TaskCompletionSource _startFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposeTask;
    public event Action<ReceiverUpdate>? Updated;
    public event Action<ReceivedFile>? FileReceived;
    public PairingInvite? Invite { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;

    public ReceiverClient(string relayUrl, string destinationFolder)
    {
        _server = Wire.ValidateServer(relayUrl);
        if (string.IsNullOrWhiteSpace(destinationFolder)) throw new ArgumentException("저장 폴더를 선택하세요.");
        _destination = Path.GetFullPath(destinationFolder);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken sessionToken;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("새 ReceiverClient로 연결을 시작하세요.");
            _started = true;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sessionToken = _cancellation.Token;
        }
        try { await StartCoreAsync(sessionToken).ConfigureAwait(false); }
        finally { _startFinished.TrySetResult(); }
    }

    private async Task StartCoreAsync(CancellationToken sessionToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_server, "api/rooms"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"중계 서버가 연결을 만들지 못했습니다. HTTP {(int)response.StatusCode}");
        // Bound the response before parsing; a custom relay must not cause unbounded memory use.
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        byte[] responseBytes = new byte[8193];
        int length = 0;
        while (length < responseBytes.Length)
        {
            int read = await body.ReadAsync(responseBytes.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > 8192) throw new InvalidDataException("중계 서버 응답이 너무 큽니다.");
        var session = JsonSerializer.Deserialize<RelaySession>(responseBytes.AsSpan(0, length), Wire.Json) ?? throw new InvalidDataException("중계 서버의 연결 정보가 잘못되었습니다.");
        if (session.RoomId is null || session.RoomId.Length != 32 || !session.RoomId.All(Uri.IsHexDigit) ||
            !ValidToken(session.ReceiverToken) || !ValidToken(session.SenderToken))
            throw new InvalidDataException("중계 서버의 연결 정보가 잘못되었습니다.");
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + session.ReceiverToken);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _socket.ConnectAsync(Wire.SocketUri(_server, session.RoomId, "receiver"), timeout.Token).ConfigureAwait(false);
        sessionToken.ThrowIfCancellationRequested();
        Invite = new PairingInvite(1, _server.GetLeftPart(UriPartial.Authority), session.RoomId, session.SenderToken, Convert.ToBase64String(_key));
        Notify(Updated, new ReceiverUpdate("waiting", "iPhone 앱에서 QR을 스캔하고 파일을 보내세요. 연결 대기는 최대 10분입니다."));
        Completion = ReceiveLoopAsync(sessionToken);
    }

    private static bool ValidToken(string? token) => token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        IncomingFile? incoming = null;
        long lastProgress = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await Wire.ReceiveAsync(_socket, cancellationToken).ConfigureAwait(false);
                if (packet is null)
                {
                    Notify(Updated, new ReceiverUpdate("disconnected", incoming is null ? "연결이 종료되었습니다. 다시 연결하려면 새 QR을 만드세요." : "연결이 끊겼습니다. 미완료 파일은 저장되지 않았습니다. 새 QR로 다시 보내세요."));
                    return;
                }
                var (type, payload) = Wire.Decrypt(packet, _key);
                switch (type)
                {
                    case Wire.Manifest:
                        if (incoming is not null) throw new InvalidDataException("진행 중인 파일이 있습니다.");
                        var manifest = JsonSerializer.Deserialize<TransferManifest>(payload, Wire.Json) ?? throw new InvalidDataException("파일 정보를 읽지 못했습니다.");
                        IncomingFile.Validate(manifest);
                        _transfers.Accept(manifest.TransferId);
                        incoming = new IncomingFile(_destination, manifest);
                        lastProgress = Environment.TickCount64;
                        Notify(Updated, new ReceiverUpdate("receiving", "파일을 받고 있습니다.", manifest.FileName, 0, manifest.PayloadSize));
                        await Wire.SendJsonAsync(_socket, Wire.Ack, new TransferAck("ready", manifest.TransferId), _key, cancellationToken).ConfigureAwait(false);
                        break;
                    case Wire.Chunk:
                        if (incoming is null) throw new InvalidDataException("파일 정보보다 데이터가 먼저 도착했습니다.");
                        await incoming.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                        if (Environment.TickCount64 - lastProgress >= 100 || incoming.BytesReceived == incoming.Manifest.PayloadSize)
                        {
                            lastProgress = Environment.TickCount64;
                            Notify(Updated, new ReceiverUpdate("receiving", "파일을 받고 있습니다.", incoming.Manifest.FileName, incoming.BytesReceived, incoming.Manifest.PayloadSize));
                        }
                        break;
                    case Wire.End:
                        var end = JsonSerializer.Deserialize<TransferEnd>(payload, Wire.Json) ?? throw new InvalidDataException("전송 완료 정보를 읽지 못했습니다.");
                        if (incoming is null || end.TransferId != incoming.Manifest.TransferId) throw new InvalidDataException("파일의 전송 번호가 일치하지 않습니다.");
                        Notify(Updated, new ReceiverUpdate("verifying", "원본 크기와 SHA-256을 검증하고 있습니다.", incoming.Manifest.FileName, incoming.BytesReceived, incoming.Manifest.PayloadSize));
                        var result = await incoming.CompleteAsync(cancellationToken).ConfigureAwait(false);
                        await incoming.DisposeAsync().ConfigureAwait(false);
                        incoming = null;
                        Notify(FileReceived, result);
                        Notify(Updated, new ReceiverUpdate("received", "검증을 마친 파일을 저장했습니다. 다음 파일을 보낼 수 있습니다.", result.Name, result.WireSize, result.WireSize));
                        // Only confirm completion after verification and atomic commit to disk.
                        await Wire.SendJsonAsync(_socket, Wire.Ack, new TransferAck("complete", end.TransferId, result.Name), _key, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException("지원하지 않는 메시지 순서입니다.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Notify(Updated, new ReceiverUpdate("disconnected", "연결을 종료했습니다. 미완료 파일은 정리됩니다."));
        }
        catch (Exception exception)
        {
            string message = exception is CryptographicException ? "파일 인증에 실패했습니다. 새 QR로 다시 연결하세요." : exception.Message;
            if (_socket.State == WebSocketState.Open)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await Wire.SendJsonAsync(_socket, Wire.Ack, new TransferAck("error", incoming?.Manifest.TransferId ?? "", Message: message), _key, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            }
            Notify(Updated, new ReceiverUpdate("error", "전송을 완료하지 못했습니다. " + message));
        }
        finally
        {
            try { if (incoming is not null) await incoming.DisposeAsync().ConfigureAwait(false); }
            finally { _socket.Abort(); }
        }
    }

    private static void Notify<T>(Action<T>? observers, T value)
    {
        if (observers is null) return;
        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try { observer(value); }
            catch (Exception error)
            {
                // A closed UI or failing subscriber must not prevent acknowledgement or cleanup.
                Trace.TraceError("PocketBridge observer failed: {0}", error.GetType().Name);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (_cancellation is not null) await _cancellation.CancelAsync().ConfigureAwait(false);
            _socket.Abort();
            if (_started) await _startFinished.Task.ConfigureAwait(false);
            await Completion.ConfigureAwait(false);
        }
        finally
        {
            _socket.Dispose();
            _cancellation?.Dispose();
            CryptographicOperations.ZeroMemory(_key);
            Invite = null;
        }
    }
}
