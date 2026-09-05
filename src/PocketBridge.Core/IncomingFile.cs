using System.IO.Compression;
using System.Security.Cryptography;

namespace PocketBridge.Core;

/// <summary>Receives untrusted bytes into an owned temporary directory, verifies, then commits without overwriting.</summary>
public sealed class IncomingFile : IAsyncDisposable
{
    private readonly string _destination;
    private readonly string _temporaryDirectory;
    private readonly string _payloadPath;
    private FileStream? _payload;
    public TransferManifest Manifest { get; }
    public long BytesReceived { get; private set; }

    public IncomingFile(string destination, TransferManifest manifest)
    {
        Validate(manifest);
        Manifest = manifest;
        _destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(_destination);
        var drive = new DriveInfo(Path.GetPathRoot(_destination)!);
        long required = checked(manifest.PayloadSize + (manifest.Compression == "zip" ? manifest.OriginalSize : 0));
        if (drive.IsReady && drive.AvailableFreeSpace < required + 32L * 1024 * 1024)
            throw new IOException("저장 공간이 부족합니다. 다른 폴더나 드라이브를 선택하세요.");
        _temporaryDirectory = Path.Combine(_destination, ".pocketbridge-" + Guid.NewGuid().ToString("N") + ".partial");
        _payloadPath = Path.Combine(_temporaryDirectory, "payload");
        Directory.CreateDirectory(_temporaryDirectory);
        try { _payload = new FileStream(_payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch { Directory.Delete(_temporaryDirectory); throw; }
    }

    public static void Validate(TransferManifest m)
    {
        if (!Guid.TryParse(m.TransferId, out _) || string.IsNullOrWhiteSpace(m.FileName) || m.FileName.Length > 1024 ||
            m.OriginalSize < 0 || m.PayloadSize < 0 || m.OriginalSize > Wire.MaxFileSize || m.PayloadSize > Wire.MaxFileSize ||
            (m.Compression != "none" && m.Compression != "zip") ||
            (m.Compression == "none" && m.OriginalSize != m.PayloadSize) ||
            m.Sha256 is null || m.Sha256.Length != 64 || !m.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("파일 정보가 올바르지 않거나 파일당 100 GiB 제한을 초과했습니다.");
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (_payload is null) throw new InvalidOperationException("이미 끝난 전송입니다.");
        if (bytes.Length == 0 || bytes.Length > Wire.ChunkSize || BytesReceived + bytes.Length > Manifest.PayloadSize)
            throw new InvalidDataException("전송한 파일 크기가 안내된 크기와 다릅니다.");
        await _payload.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        BytesReceived += bytes.Length;
    }

    public async Task<ReceivedFile> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_payload is null) throw new InvalidOperationException("이미 끝난 전송입니다.");
        if (BytesReceived != Manifest.PayloadSize) throw new InvalidDataException("파일이 끝까지 도착하지 않았습니다.");
        await _payload.FlushAsync(cancellationToken).ConfigureAwait(false);
        _payload.Flush(flushToDisk: true);
        await _payload.DisposeAsync().ConfigureAwait(false);
        _payload = null;
        string verifiedPath = _payloadPath;
        if (Manifest.Compression == "zip")
        {
            verifiedPath = Path.Combine(_temporaryDirectory, "original");
            await using var source = File.OpenRead(_payloadPath);
            cancellationToken.ThrowIfCancellationRequested();
            ZipMetadataGuard.ValidateSingleEntry(source);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read);
            if (archive.Entries.Count != 1 || archive.Entries[0].Name.Length == 0 || archive.Entries[0].Length != Manifest.OriginalSize)
                throw new InvalidDataException("압축 파일 구조나 원본 크기가 올바르지 않습니다.");
            await using var entry = archive.Entries[0].Open();
            await using var extracted = new FileStream(verifiedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            byte[] buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await entry.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > Manifest.OriginalSize) throw new InvalidDataException("압축 해제 크기 제한을 초과했습니다.");
                await extracted.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (total != Manifest.OriginalSize) throw new InvalidDataException("압축 해제한 파일 크기가 올바르지 않습니다.");
            await extracted.FlushAsync(cancellationToken).ConfigureAwait(false);
            extracted.Flush(flushToDisk: true);
        }
        await using (var original = File.OpenRead(verifiedPath))
        {
            if (original.Length != Manifest.OriginalSize) throw new InvalidDataException("원본 크기가 일치하지 않습니다.");
            byte[] hash = await SHA256.HashDataAsync(original, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(Manifest.Sha256)))
                throw new InvalidDataException("파일 검증에 실패했습니다. 다시 전송하세요.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        string name = SafeName(Manifest.FileName);
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        for (int suffix = 0; suffix < 10000; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string finalName = suffix == 0 ? name : $"{stem} ({suffix}){extension}";
            string fullPath = Path.Combine(_destination, finalName);
            try
            {
                File.Move(verifiedPath, fullPath, overwrite: false);
                return new ReceivedFile(finalName, fullPath, Manifest.OriginalSize, Manifest.PayloadSize, DateTimeOffset.Now, Manifest.Sha256.ToLowerInvariant());
            }
            catch (IOException) when (File.Exists(fullPath) || Directory.Exists(fullPath)) { }
        }
        throw new IOException("같은 이름의 파일이 너무 많습니다. 다른 저장 폴더를 선택하세요.");
    }

    public static string SafeName(string input)
    {
        string name = input.Replace('\\', '/').Split('/').Last();
        char[] invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
        name = new string(name.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name is "." or "..") name = "file";
        string stem = name.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && ("123456789¹²³".Contains(stem[3])))) name = "_" + name;
        if (name.Length > 140)
        {
            string extension = Path.GetExtension(name);
            string baseName = Path.GetFileNameWithoutExtension(name);
            if (extension.Length > 20) extension = extension[..20];
            if (baseName.Length == 0) baseName = "file";
            name = baseName[..Math.Min(baseName.Length, 140 - extension.Length)] + extension;
        }
        return name;
    }

    public async ValueTask DisposeAsync()
    {
        if (_payload is not null)
        {
            try { await _payload.DisposeAsync().ConfigureAwait(false); }
            catch (IOException) { /* Cleanup still runs after a failed disk flush. */ }
            finally { _payload = null; }
        }
        // This random directory is owned exclusively by this instance; no user paths are traversed.
        try { if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
