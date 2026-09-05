using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace PocketBridge.Core;

/// <summary>Receives files uploaded by an Apple Shortcut through a trusted HTTPS relay.</summary>
public sealed class ShortcutReceiverClient : IAsyncDisposable
{
    private readonly Uri _server;
    private readonly string _destination;
    private readonly ClientWebSocket _socket = new();
    private readonly TransferLedger _ledger = new();
    private readonly object _lifecycle = new();
    private readonly TaskCompletionSource _startFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cancellation;
    private Task? _disposeTask;
    private bool _started;
    private bool _disposed;

    public event Action<ReceiverUpdate>? Updated;
    public event Action<ReceivedFile>? FileReceived;
    public ShortcutInvite? Invite { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;

    public ShortcutReceiverClient(string relayUrl, string destinationFolder)
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
            if (_started) throw new InvalidOperationException("새 연결 객체로 다시 시작하세요.");
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
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_server, "api/shortcut/rooms"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"중계 서버가 단축어 연결을 만들지 못했습니다. HTTP {(int)response.StatusCode}");
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
        var session = JsonSerializer.Deserialize<RelaySession>(responseBytes.AsSpan(0, length), Wire.Json) ?? throw new InvalidDataException("중계 서버 연결 정보가 잘못되었습니다.");
        if (session.RoomId is null || session.RoomId.Length != 32 || !session.RoomId.All(Uri.IsHexDigit) ||
            !ValidToken(session.ReceiverToken) || !ValidToken(session.SenderToken))
            throw new InvalidDataException("중계 서버 연결 정보가 잘못되었습니다.");
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + session.ReceiverToken);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _socket.ConnectAsync(ShortcutWire.SocketUri(_server, session.RoomId), timeout.Token).ConfigureAwait(false);
        sessionToken.ThrowIfCancellationRequested();
        Invite = new ShortcutInvite(1, _server.GetLeftPart(UriPartial.Authority), session.RoomId, session.SenderToken);
        Notify(Updated, new ReceiverUpdate("waiting", "사진이나 파일을 공유해 PocketBridge 단축어를 누른 뒤 이 QR을 스캔하세요. 연결 대기는 최대 10분입니다."));
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
                byte[]? packet = await Wire.ReceiveAsync(_socket, cancellationToken).ConfigureAwait(false);
                if (packet is null)
                {
                    Notify(Updated, new ReceiverUpdate("disconnected", incoming is null ? "연결이 종료되었습니다. 새 QR을 만들어 다시 보낼 수 있습니다." : "연결이 끊겨 미완료 파일을 저장하지 않았습니다."));
                    return;
                }
                var (type, payload) = ShortcutWire.Decode(packet);
                switch (type)
                {
                    case Wire.Manifest:
                        if (incoming is not null) throw new InvalidDataException("진행 중인 파일이 있습니다.");
                        var manifest = JsonSerializer.Deserialize<TransferManifest>(payload, Wire.Json) ?? throw new InvalidDataException("파일 정보를 읽지 못했습니다.");
                        _ledger.Accept(manifest.TransferId);
                        incoming = new IncomingFile(_destination, manifest);
                        lastProgress = Environment.TickCount64;
                        Notify(Updated, new ReceiverUpdate("receiving", "단축어에서 파일을 받고 있습니다.", manifest.FileName, 0, manifest.PayloadSize));
                        await ShortcutWire.SendJsonAsync(_socket, Wire.Ack, new TransferAck("ready", manifest.TransferId), cancellationToken).ConfigureAwait(false);
                        break;
                    case Wire.Chunk:
                        if (incoming is null) throw new InvalidDataException("파일 정보보다 데이터가 먼저 도착했습니다.");
                        await incoming.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                        if (Environment.TickCount64 - lastProgress >= 100 || incoming.BytesReceived == incoming.Manifest.PayloadSize)
                        {
                            lastProgress = Environment.TickCount64;
                            Notify(Updated, new ReceiverUpdate("receiving", "단축어에서 파일을 받고 있습니다.", incoming.Manifest.FileName, incoming.BytesReceived, incoming.Manifest.PayloadSize));
                        }
                        break;
                    case Wire.End:
                        var end = JsonSerializer.Deserialize<TransferEnd>(payload, Wire.Json) ?? throw new InvalidDataException("전송 완료 정보를 읽지 못했습니다.");
                        if (incoming is null || end.TransferId != incoming.Manifest.TransferId) throw new InvalidDataException("파일의 전송 번호가 일치하지 않습니다.");
                        Notify(Updated, new ReceiverUpdate("verifying", "파일 크기와 iPhone에서 계산한 SHA-256을 확인하고 있습니다.", incoming.Manifest.FileName, incoming.BytesReceived, incoming.Manifest.PayloadSize));
                        var result = await incoming.CompleteAsync(cancellationToken).ConfigureAwait(false);
                        await incoming.DisposeAsync().ConfigureAwait(false);
                        incoming = null;
                        Notify(FileReceived, result);
                        Notify(Updated, new ReceiverUpdate("received", "검증을 마친 파일을 저장했습니다. 다음 파일을 보낼 수 있습니다.", result.Name, result.WireSize, result.WireSize));
                        await ShortcutWire.SendJsonAsync(_socket, Wire.Ack, new TransferAck("complete", end.TransferId, result.Name), cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException("지원하지 않는 단축어 메시지 순서입니다.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Notify(Updated, new ReceiverUpdate("disconnected", "연결을 종료했습니다. 미완료 파일은 정리됩니다."));
        }
        catch (Exception exception)
        {
            string message = exception.Message;
            if (_socket.State == WebSocketState.Open)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ShortcutWire.SendJsonAsync(_socket, Wire.Ack, new TransferAck("error", incoming?.Manifest.TransferId ?? "", Message: message), timeout.Token).ConfigureAwait(false);
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
            catch (Exception error) { Trace.TraceError("PocketBridge observer failed: {0}", error.GetType().Name); }
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
            Invite = null;
        }
    }
}
