using System.IO.Compression;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PocketBridge.Core;

// Dependency-free executable tests: dotnet run --project tests/PocketBridge.Tests
// Add --relay http://localhost:5080 for real two-socket integration through a running relay.
string testRoot = Path.Combine(Path.GetTempPath(), "PocketBridge-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
int passed = 0;
async Task Check(string name, Func<Task> test)
{
    await test();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
}
async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
TransferManifest Manifest(byte[] original, string name = "document.txt", byte[]? wire = null) => new(Guid.NewGuid().ToString(), name, original.Length, (wire ?? original).Length, wire is null ? "none" : "zip", Convert.ToHexStringLower(SHA256.HashData(original)));
byte[] Zip(byte[] bytes, bool extra = false)
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        using (var stream = archive.CreateEntry("../../ignored-name.txt", CompressionLevel.Optimal).Open()) stream.Write(bytes);
        if (extra) { using var entry = archive.CreateEntry("second.txt").Open(); entry.WriteByte(1); }
    }
    return output.ToArray();
}
async Task WriteAll(IncomingFile file, byte[] bytes)
{
    for (int i = 0; i < bytes.Length; i += Wire.ChunkSize)
        await file.WriteAsync(bytes.AsMemory(i, Math.Min(Wire.ChunkSize, bytes.Length - i)), default);
}
string Folder() => Path.Combine(testRoot, Guid.NewGuid().ToString("N"));

try
{
    await Check("AES-256-GCM NIST vector decrypt", () =>
    {
        byte[] packet = Convert.FromHexString("01" + new string('0', 24) + "cea7403d4d606b6e074ec5d3baf39d18" + "d0d1c8a799996bf0265b98b5d48ab919");
        var decoded = Wire.Decrypt(packet, new byte[32]);
        Equal((byte)0, decoded.Type);
        Equal(new string('0', 30), Convert.ToHexString(decoded.Payload));
        return Task.CompletedTask;
    });
    await Check("Encrypted message roundtrip with random nonces", () =>
    {
        byte[] key = RandomNumberGenerator.GetBytes(32), data = RandomNumberGenerator.GetBytes(Wire.ChunkSize);
        var first = Wire.Encrypt(Wire.Chunk, data, key);
        var second = Wire.Encrypt(Wire.Chunk, data, key);
        if (first.SequenceEqual(second)) throw new Exception("Nonce reuse");
        var (type, decoded) = Wire.Decrypt(first, key);
        Equal(Wire.Chunk, type);
        Equal(Convert.ToHexString(data), Convert.ToHexString(decoded));
        return Task.CompletedTask;
    });
    await Check("Ciphertext tampering is rejected", async () =>
    {
        byte[] key = RandomNumberGenerator.GetBytes(32), packet = Wire.Encrypt(Wire.Chunk, [1, 2, 3], key);
        packet[14] ^= 1;
        await Reject<CryptographicException>(() => { Wire.Decrypt(packet, key); return Task.CompletedTask; });
    });
    await Check("Wrong key is rejected", async () =>
    {
        byte[] packet = Wire.Encrypt(Wire.Chunk, [1], RandomNumberGenerator.GetBytes(32));
        await Reject<CryptographicException>(() => { Wire.Decrypt(packet, RandomNumberGenerator.GetBytes(32)); return Task.CompletedTask; });
    });
    await Check("HTTPS relay validation prevents downgrade and URL credentials", async () =>
    {
        Equal("relay.example.com", Wire.ValidateServer("https://relay.example.com").Host);
        Equal("localhost", Wire.ValidateServer("http://localhost:5080").Host);
        foreach (string bad in new[] { "http://public.example.com", "https://user:password@example.com", "file:///x", "https://example.com/path", "https://example.com?key=secret" })
            await Reject<ArgumentException>(() => { Wire.ValidateServer(bad); return Task.CompletedTask; });
    });
    await Check("Relay response is bounded before full HTTP body buffering", async () =>
    {
        await WithHttpServer(async (uri, requestReceived, ct) =>
        {
            await using var receiver = new ReceiverClient(uri.ToString(), Folder());
            await Reject<InvalidDataException>(() => receiver.StartAsync(ct));
        }, sendOversizedPrefix: true);
    });
    await Check("Dispose during room creation cancels and waits for startup", async () =>
    {
        await WithHttpServer(async (uri, requestReceived, ct) =>
        {
            var receiver = new ReceiverClient(uri.ToString(), Folder());
            var start = receiver.StartAsync(ct);
            await requestReceived.WaitAsync(ct);
            await receiver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
            await Reject<OperationCanceledException>(() => start);
            await receiver.DisposeAsync();
            Equal<PairingInvite?>(null, receiver.Invite);
        }, sendOversizedPrefix: false);
    });
    await Check("Windows unsafe names become a basename", () =>
    {
        Equal("file.txt", IncomingFile.SafeName("../../file.txt"));
        Equal("file.txt", IncomingFile.SafeName("C:\\Users\\someone\\file.txt"));
        Equal("_CON.txt", IncomingFile.SafeName("CON.txt"));
        Equal("file", IncomingFile.SafeName(".."));
        if (IncomingFile.SafeName("." + new string('x', 400)).Length > 140) throw new Exception("Name too long");
        return Task.CompletedTask;
    });
    await Check("Raw file hash/size verified, original bytes preserved", async () =>
    {
        string folder = Folder(); byte[] original = RandomNumberGenerator.GetBytes(700000);
        await using var file = new IncomingFile(folder, Manifest(original, "여행 사진.heic"));
        await WriteAll(file, original);
        var saved = await file.CompleteAsync(default);
        Equal(Convert.ToHexString(original), Convert.ToHexString(await File.ReadAllBytesAsync(saved.FullPath)));
    });
    await Check("ZIP document restored and archive traversal name ignored", async () =>
    {
        byte[] original = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("중요한 문서 내용,hello world\n", 10000)));
        byte[] zipped = Zip(original); string folder = Folder();
        await using var file = new IncomingFile(folder, Manifest(original, "보고서.csv", zipped));
        await WriteAll(file, zipped);
        var saved = await file.CompleteAsync(default);
        Equal("보고서.csv", saved.Name);
        Equal(Convert.ToHexString(original), Convert.ToHexString(await File.ReadAllBytesAsync(saved.FullPath)));
        if (zipped.Length >= original.Length) throw new Exception("Document did not compress");
    });
    await Check("Existing files never overwritten", async () =>
    {
        string folder = Folder(); Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "same.txt"), "keep");
        byte[] original = Encoding.UTF8.GetBytes("new");
        await using var file = new IncomingFile(folder, Manifest(original, "same.txt"));
        await WriteAll(file, original);
        var saved = await file.CompleteAsync(default);
        Equal("same (1).txt", saved.Name);
        Equal("keep", await File.ReadAllTextAsync(Path.Combine(folder, "same.txt")));
    });
    await Check("Empty files commit correctly", async () =>
    {
        await using var file = new IncomingFile(Folder(), Manifest([]));
        var saved = await file.CompleteAsync(default); Equal(0L, new FileInfo(saved.FullPath).Length);
    });
    await Check("Incomplete file is rejected and temporary file cleaned", async () =>
    {
        string folder = Folder();
        await using (var file = new IncomingFile(folder, Manifest(new byte[10])))
        {
            await file.WriteAsync(new byte[5], default);
            await Reject<InvalidDataException>(() => file.CompleteAsync(default));
        }
        Equal(0, Directory.GetFileSystemEntries(folder).Length);
    });
    await Check("Wrong checksum never commits", async () =>
    {
        string folder = Folder();
        await using (var file = new IncomingFile(folder, Manifest([1, 2, 3])))
        {
            await file.WriteAsync(new byte[] { 3, 2, 1 }, default);
            await Reject<InvalidDataException>(() => file.CompleteAsync(default));
        }
        Equal(0, Directory.GetFileSystemEntries(folder).Length);
    });
    await Check("Extra ZIP entries rejected", async () =>
    {
        byte[] original = [1, 2, 3], zip = Zip(original, true);
        await using var file = new IncomingFile(Folder(), Manifest(original, wire: zip));
        await WriteAll(file, zip);
        await Reject<InvalidDataException>(() => file.CompleteAsync(default));
    });
    await Check("Forged ZIP entry count is rejected before metadata allocation", async () =>
    {
        byte[] original = [1, 2, 3], zip = Zip(original, true);
        int footer = zip.Length - 22;
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(footer + 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(footer + 10), 1);
        await using var file = new IncomingFile(Folder(), Manifest(original, wire: zip));
        await WriteAll(file, zip);
        await Reject<InvalidDataException>(() => file.CompleteAsync(default));
    });
    await Check("ZIP64 single-entry metadata remains compatible", async () =>
    {
        byte[] original = Encoding.UTF8.GetBytes(new string('a', 10000));
        byte[] simple = Zip(original);
        int footer = simple.Length - 22;
        byte[] zip = new byte[simple.Length + 76];
        simple.AsSpan(0, footer).CopyTo(zip);
        var zip64 = zip.AsSpan(footer, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(zip64, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[4..], 44);
        BinaryPrimitives.WriteUInt16LittleEndian(zip64[12..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(zip64[14..], 45);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[24..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[32..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[40..], BinaryPrimitives.ReadUInt32LittleEndian(simple.AsSpan(footer + 12)));
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[48..], BinaryPrimitives.ReadUInt32LittleEndian(simple.AsSpan(footer + 16)));
        var locator = zip.AsSpan(footer + 56, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator[8..], (ulong)footer);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
        simple.AsSpan(footer).CopyTo(zip.AsSpan(footer + 76));
        var end = zip.AsSpan(footer + 76);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], uint.MaxValue);
        await using var file = new IncomingFile(Folder(), Manifest(original, wire: zip));
        await WriteAll(file, zip);
        var received = await file.CompleteAsync(default);
        Equal(Convert.ToHexString(original), Convert.ToHexString(await File.ReadAllBytesAsync(received.FullPath)));
    });
    await Check("ZIP declared expansion limit enforced", async () =>
    {
        byte[] original = new byte[10000], zip = Zip(original);
        await using var file = new IncomingFile(Folder(), Manifest(original, wire: zip) with { OriginalSize = 1 });
        await WriteAll(file, zip);
        await Reject<InvalidDataException>(() => file.CompleteAsync(default));
    });
    await Check("Overflowing and oversized chunks rejected", async () =>
    {
        await using var file = new IncomingFile(Folder(), Manifest([1]));
        await Reject<InvalidDataException>(() => file.WriteAsync(new byte[2], default));
        await using var large = new IncomingFile(Folder(), Manifest(new byte[Wire.ChunkSize * 2]));
        await Reject<InvalidDataException>(() => large.WriteAsync(new byte[Wire.ChunkSize + 1], default));
    });
    await Check("Cancel before commit keeps no completed file", async () =>
    {
        string folder = Folder();
        await using (var file = new IncomingFile(folder, Manifest([1])))
        {
            await file.WriteAsync(new byte[] { 1 }, default);
            await Reject<OperationCanceledException>(() => file.CompleteAsync(new CancellationToken(true)));
        }
        Equal(0, Directory.GetFileSystemEntries(folder).Length);
    });
    await Check("Invalid manifest size, hash and compression rejected", async () =>
    {
        foreach (var bad in new[] { Manifest([1]) with { OriginalSize = -1 }, Manifest([1]) with { PayloadSize = Wire.MaxFileSize + 1 }, Manifest([1]) with { Sha256 = "bad" }, Manifest([1]) with { Compression = "rar" } })
            await Reject<InvalidDataException>(() => { IncomingFile.Validate(bad); return Task.CompletedTask; });
    });
    await Check("A transfer ID cannot replay within a pairing session", async () =>
    {
        var ledger = new TransferLedger();
        var id = Guid.NewGuid();
        ledger.Accept(id.ToString("D"));
        await Reject<InvalidDataException>(() => { ledger.Accept(id.ToString("D")); return Task.CompletedTask; });
        await Reject<InvalidDataException>(() => { ledger.Accept(id.ToString("B").ToUpperInvariant()); return Task.CompletedTask; });
        // A new pairing has its own ledger; interrupted transfers may be retried there.
        new TransferLedger().Accept(id.ToString("D"));
    });
    await Check("Pairing accepts 10,000 unique IDs then requires a new pairing", async () =>
    {
        var ledger = new TransferLedger();
        for (int i = 0; i < Wire.MaxTransfersPerSession; i++) ledger.Accept(Guid.NewGuid().ToString());
        await Reject<InvalidDataException>(() => { ledger.Accept(Guid.NewGuid().ToString()); return Task.CompletedTask; });
    });
    int relayOption = Array.IndexOf(args, "--relay");
    if (relayOption >= 0 && args.Length > relayOption + 1)
    {
        string server = args[relayOption + 1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;
        string folder = Folder();
        await using var receiver = new ReceiverClient(server, folder);
        var receipts = new List<ReceivedFile>();
        // A failing UI observer must neither strand the receive loop nor prevent the other subscriber.
        receiver.Updated += _ => throw new InvalidOperationException("Simulated closed dispatcher");
        receiver.FileReceived += _ => throw new InvalidOperationException("Simulated receipt subscriber failure");
        receiver.FileReceived += receipt => { lock (receipts) receipts.Add(receipt); };
        await receiver.StartAsync(ct);
        var invite = receiver.Invite!;
        byte[] key = Convert.FromBase64String(invite.Key);
        using var sender = new ClientWebSocket();
        sender.Options.SetRequestHeader("Authorization", "Bearer " + invite.Token);
        await sender.ConnectAsync(Wire.SocketUri(Wire.ValidateServer(server), invite.Room, "sender"), ct);
        async Task<TransferAck> Ack()
        {
            var packet = await Wire.ReceiveAsync(sender, ct) ?? throw new Exception("Peer disconnected");
            var message = Wire.Decrypt(packet, key); Equal(Wire.Ack, message.Type);
            return JsonSerializer.Deserialize<TransferAck>(message.Payload, Wire.Json)!;
        }
        async Task Send(byte[] original, byte[]? payload = null)
        {
            var manifest = Manifest(original, "integration.txt", payload);
            await Wire.SendJsonAsync(sender, Wire.Manifest, manifest, key, ct);
            var ready = await Ack(); Equal("ready", ready.Kind); Equal(manifest.TransferId, ready.TransferId);
            byte[] wire = payload ?? original;
            for (int i = 0; i < wire.Length; i += Wire.ChunkSize)
                await sender.SendAsync(new ArraySegment<byte>(Wire.Encrypt(Wire.Chunk, wire.AsSpan(i, Math.Min(Wire.ChunkSize, wire.Length - i)), key)), WebSocketMessageType.Binary, true, ct);
            await Wire.SendJsonAsync(sender, Wire.End, new TransferEnd(manifest.TransferId), key, ct);
            var complete = await Ack(); Equal("complete", complete.Kind); Equal(manifest.TransferId, complete.TransferId);
            Equal(Convert.ToHexString(original), Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(folder, complete.FileName!), ct)));
        }
        await Check("Real relay: encrypted raw transfer >1MiB", () => Send(RandomNumberGenerator.GetBytes(1200000)));
        await Check("Real relay: compressed document and filename collision", async () =>
        {
            byte[] original = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a,b,c,d,e\n", 10000)));
            await Send(original, Zip(original));
            if (!File.Exists(Path.Combine(folder, "integration (1).txt"))) throw new Exception("Collision not resolved");
        });
        await Check("Real relay: zero byte transfer and receipt count", async () => { await Send([]); lock (receipts) Equal(3, receipts.Count); });
        await Check("Real relay: disconnect cleans partial file", async () =>
        {
            await Wire.SendJsonAsync(sender, Wire.Manifest, Manifest(new byte[1000], "interrupted.bin"), key, ct);
            Equal("ready", (await Ack()).Kind);
            await sender.SendAsync(new ArraySegment<byte>(Wire.Encrypt(Wire.Chunk, new byte[500], key)), WebSocketMessageType.Binary, true, ct);
            await sender.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test disconnect", ct);
            await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Equal(3, Directory.GetFileSystemEntries(folder).Length);
            Equal(false, File.Exists(Path.Combine(folder, "interrupted.bin")));
        });
    }
    Console.WriteLine($"All {passed} checks passed.");
}
finally
{
    // Only delete this test run's random, absolute, owned directory.
    if (Path.GetFullPath(testRoot).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(testRoot, recursive: true);
}

async Task WithHttpServer(Func<Uri, Task, CancellationToken, Task> action, bool sendOversizedPrefix)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var server = Serve();
    try { await action(new Uri($"http://127.0.0.1:{endpoint.Port}"), requestReceived.Task, timeout.Token); }
    finally
    {
        await timeout.CancelAsync();
        listener.Stop();
        try { await server; } catch (OperationCanceledException) { }
    }
    async Task Serve()
    {
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var stream = client.GetStream();
        byte[] request = new byte[1024];
        int length = 0;
        while (length < request.Length)
        {
            int count = await stream.ReadAsync(request.AsMemory(length), timeout.Token);
            if (count == 0) return;
            length += count;
            if (Encoding.ASCII.GetString(request, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        requestReceived.TrySetResult();
        if (sendOversizedPrefix)
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 104857600\r\nContent-Type: application/json\r\n\r\n"u8.ToArray(), timeout.Token);
            await stream.WriteAsync(new byte[8193], timeout.Token);
            await stream.FlushAsync(timeout.Token);
        }
        // Deliberately keep an incomplete HTTP response open. A bounded reader must reject its prefix.
        await Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
    }
}
