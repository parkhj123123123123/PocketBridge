using System.Text;
using PocketBridge.Core;

namespace PocketBridge.Relay;

public sealed record ShortcutUploadMetadata(string Name, long OriginalSize, long PayloadSize, string Compression, string Sha256)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ShortcutUploadMetadata Parse(HttpRequest request)
    {
        string encodedName = Header(request, "X-PocketBridge-Name64", 4096);
        string name;
        try { name = StrictUtf8.GetString(Convert.FromBase64String(encodedName)); }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        {
            throw new BadHttpRequestException("X-PocketBridge-Name64 must be a Base64 UTF-8 file name.");
        }
        string compression = Header(request, "X-PocketBridge-Compression", 8).ToLowerInvariant();
        string hash = Header(request, "X-PocketBridge-SHA256", 64).ToLowerInvariant();
        if (!long.TryParse(Header(request, "X-PocketBridge-Original-Size", 20), out long originalSize) ||
            !long.TryParse(Header(request, "X-PocketBridge-Payload-Size", 20), out long payloadSize))
            throw new BadHttpRequestException("File sizes must be decimal integers.");
        var metadata = new ShortcutUploadMetadata(name, originalSize, payloadSize, compression, hash);
        // Reuse the receiver's complete bounds and shape validation before reading a potentially large request body.
        IncomingFile.Validate(new TransferManifest(Guid.NewGuid().ToString(), name, originalSize, payloadSize, compression, hash));
        if (request.ContentLength is long contentLength && contentLength != payloadSize)
            throw new BadHttpRequestException("Content-Length does not match X-PocketBridge-Payload-Size.");
        return metadata;
    }

    private static string Header(HttpRequest request, string name, int maximumLength)
    {
        string value = request.Headers[name].ToString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || request.Headers[name].Count != 1)
            throw new BadHttpRequestException($"Missing or invalid {name} header.");
        return value;
    }
}
