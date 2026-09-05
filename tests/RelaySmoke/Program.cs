using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
if (root is null) throw new InvalidOperationException("Run from inside the repository.");
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
var relayDll = Path.Combine(root.FullName, "src", "PocketBridge.Relay", "bin", configuration, "net10.0", "PocketBridge.Relay.dll");
using var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
var url = new Uri($"http://127.0.0.1:{port}");
var start = new ProcessStartInfo("dotnet")
{
    UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardOutput = true, RedirectStandardError = true,
    WorkingDirectory = Path.GetDirectoryName(relayDll)!
};
start.ArgumentList.Add(relayDll);
start.ArgumentList.Add("--urls");
start.ArgumentList.Add(url.ToString());
start.Environment["Relay__MaxRooms"] = "4";
using var server = Process.Start(start) ?? throw new InvalidOperationException("Could not start relay.");
var stdout = server.StandardOutput.ReadToEndAsync();
var stderr = server.StandardError.ReadToEndAsync();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var ct = timeout.Token;
using var http = new HttpClient { BaseAddress = url };
var passed = 0;
try
{
    for (var attempt = 0; ; attempt++)
    {
        try { using var ready = await http.GetAsync("/health", ct); if (ready.IsSuccessStatusCode) break; }
        catch (HttpRequestException) when (attempt < 100) { }
        await Task.Delay(100, ct);
    }
    Check((await http.GetFromJsonAsync<Health>("/health", ct))?.Protocol == 1, "health identifies protocol");
    var room = await Create();
    Check(room.RoomId.Length == 32 && room.ReceiverToken.Length == 43 && room.SenderToken.Length == 43,
        "cryptographic room and independent role credentials");
    await Reject(room, "receiver", room.SenderToken, "wrong-role token rejected");
    using var receiver = await Connect(room, "receiver", room.ReceiverToken);
    await Reject(room, "receiver", room.ReceiverToken, "duplicate role rejected");
    using var sender = await Connect(room, "sender", room.SenderToken);
    var payload = RandomNumberGenerator.GetBytes(256 * 1024 + 29);
    var read = ReadMessage(receiver);
    await sender.SendAsync(payload.AsMemory(0, 12_000), WebSocketMessageType.Binary, false, ct);
    await sender.SendAsync(payload.AsMemory(12_000), WebSocketMessageType.Binary, true, ct);
    var receivedPayload = await read;
    Check(payload.SequenceEqual(receivedPayload), "fragmented binary payload forwarded unchanged");
    var ack = RandomNumberGenerator.GetBytes(96);
    await receiver.SendAsync(ack, WebSocketMessageType.Binary, true, ct);
    var receivedAck = await ReadMessage(sender);
    Check(ack.SequenceEqual(receivedAck), "reverse acknowledgement forwarded unchanged");
    var maximum = RandomNumberGenerator.GetBytes(1024 * 1024);
    var readMaximum = ReadMessage(receiver);
    await sender.SendAsync(maximum, WebSocketMessageType.Binary, true, ct);
    var receivedMaximum = await readMaximum;
    Check(maximum.SequenceEqual(receivedMaximum), "exact 1 MiB message limit accepted");
    sender.Abort();
    await ExpectDisconnect(receiver, "disconnect closes peer");
    await Reject(room, "sender", room.SenderToken, "consumed room cannot reconnect");

    var textRoom = await Create();
    using var textReceiver = await Connect(textRoom, "receiver", textRoom.ReceiverToken);
    using var textSender = await Connect(textRoom, "sender", textRoom.SenderToken);
    await textSender.SendAsync("plaintext must not pass"u8.ToArray(), WebSocketMessageType.Text, true, ct);
    await ExpectDisconnect(textReceiver, "plaintext control messages rejected");

    var bigRoom = await Create();
    using var bigReceiver = await Connect(bigRoom, "receiver", bigRoom.ReceiverToken);
    using var bigSender = await Connect(bigRoom, "sender", bigRoom.SenderToken);
    var oversizedReceive = ExpectDisconnect(bigReceiver, "over-limit message disconnects without completing");
    try { await bigSender.SendAsync(new byte[1024 * 1024 + 1], WebSocketMessageType.Binary, true, ct); }
    catch (WebSocketException) { }
    await oversizedReceive;

    for (var i = 0; i < 4; i++) await Create();
    using var atCapacity = await http.PostAsync("/api/rooms", null, ct);
    Check(atCapacity.StatusCode == HttpStatusCode.ServiceUnavailable, "room capacity enforced");
    for (var i = 0; i < 2; i++)
    {
        using var capacity = await http.PostAsync("/api/rooms", null, ct);
        Check(capacity.StatusCode == HttpStatusCode.ServiceUnavailable, "capacity rejects without allocating");
    }
    using var limited = await http.PostAsync("/api/rooms", null, ct);
    Check(limited.StatusCode == HttpStatusCode.TooManyRequests, "room creation rate enforced");
    Console.WriteLine($"PASS: {passed} relay checks through real HTTP and WebSockets.");
}
finally
{
    if (!server.HasExited) server.Kill(entireProcessTree: true);
    await server.WaitForExitAsync();
    await Task.WhenAll(stdout, stderr);
}

void Check(bool success, string description)
{
    if (!success) throw new InvalidOperationException(description);
    passed++;
    Console.WriteLine($"PASS {description}");
}

async Task<Room> Create()
{
    using var response = await http.PostAsync("/api/rooms", null, ct);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<Room>(ct))!;
}

async Task<ClientWebSocket> Connect(Room room, string role, string token)
{
    var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
    try
    {
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/{room.RoomId}/{role}"), ct);
        return socket;
    }
    catch { socket.Dispose(); throw; }
}

async Task Reject(Room room, string role, string token, string description)
{
    try { using var unexpected = await Connect(room, role, token); }
    catch (WebSocketException) { Check(true, description); return; }
    throw new InvalidOperationException(description);
}

async Task<byte[]> ReadMessage(ClientWebSocket socket)
{
    var buffer = new byte[8192];
    using var output = new MemoryStream();
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, ct);
        if (result.MessageType != WebSocketMessageType.Binary) throw new InvalidOperationException("Expected binary.");
        output.Write(buffer, 0, result.Count);
        if (result.EndOfMessage) return output.ToArray();
    }
}

async Task ExpectDisconnect(ClientWebSocket socket, string description)
{
    var buffer = new byte[65536];
    try
    {
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.EndOfMessage) throw new InvalidOperationException("Rejected message was delivered completely.");
        }
    }
    catch (WebSocketException) { }
    Check(true, description);
}

sealed record Health(string Status, int Protocol);
sealed record Room(string RoomId, string ReceiverToken, string SenderToken, DateTimeOffset ExpiresAt);
